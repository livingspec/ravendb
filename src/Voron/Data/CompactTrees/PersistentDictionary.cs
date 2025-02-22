﻿using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Sparrow;
using Sparrow.Server.Compression;
using Voron.Exceptions;
using Voron.Global;
using Voron.Impl;
using Voron.Impl.Paging;
using static Sparrow.Hashing;

namespace Voron.Data.CompactTrees
{
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public unsafe struct PersistentDictionaryRootHeader
    {
        [FieldOffset(0)]
        public RootObjectType RootObjectType;

        [FieldOffset(1)]
        public long PageNumber;
    }
    
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public unsafe struct PersistentDictionaryHeader
    {
        public const int SizeOf = 32;

        [FieldOffset(0)]        
        public ulong TableHash;
        [FieldOffset(8)]
        public int TableSize;
        [FieldOffset(16)]
        public long CurrentId;        
        [FieldOffset(24)]
        public long PreviousId;

        public override string ToString()
        {
            return $"{nameof(TableHash)}: {TableHash}, {nameof(TableSize)}: {TableSize}, {nameof(CurrentId)}: {CurrentId}, {nameof(PreviousId)}: {PreviousId}";
        }
    }

    public unsafe partial class PersistentDictionary 
    {
        private readonly Page _page;
        
        public long PageNumber => _page.PageNumber;
        
        private readonly HopeEncoder<Encoder3Gram<NativeMemoryEncoderState>> _encoder;
        private byte[] _tempBuffer;

        public static long CreateDefault(LowLevelTransaction llt)
        {
            using var _ = Slice.From(llt.Allocator, $"{nameof(PersistentDictionary)}.Default", out var defaultKey);

            long pageNumber;

            var result = llt.RootObjects.DirectRead(defaultKey);
            if (result != null)
            {
                pageNumber = ((PersistentDictionaryRootHeader*)result)->PageNumber;
            }
            else
            {
                var numberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(DefaultAllocationSizeForTable);
                var p = llt.AllocatePage(numberOfPages);
                p.Flags = PageFlags.Overflow;
                p.OverflowSize = DefaultAllocationSizeForTable;

                PersistentDictionaryHeader* header = (PersistentDictionaryHeader*)p.DataPointer;
                header->TableSize = DefaultDictionaryTableSize;
                header->CurrentId = p.PageNumber;
                header->PreviousId = 0;

                // We retrieve the embeeded file from the assembly, copy and checksum the entire thing.             
                var embeddedFile = typeof(PersistentDictionary).Assembly.GetManifestResourceStream($"Voron.Data.CompactTrees.dictionary.bin");
                if (embeddedFile == null)
                    VoronUnrecoverableErrorException.Raise(llt.Environment.Options, "The default dictionary has not been included in the build, the build process needs to be corrected.");

                var dictionary = new byte[embeddedFile.Length];
                embeddedFile.Read(dictionary);

                var arrayAsInt = MemoryMarshal.Cast<byte, int>(dictionary.AsSpan());
                int tableSize = arrayAsInt[0];
                if (tableSize != DefaultDictionaryTableSize)
                    VoronUnrecoverableErrorException.Raise(llt.Environment.Options, "There is an inconsistency between the expected size of the default compression dictionary and the one read from storage.");

                dictionary.AsSpan()
                    .Slice(4) // Discard the table size.
                    .CopyTo(new Span<byte>(p.DataPointer + sizeof(PersistentDictionaryHeader), DefaultDictionaryTableSize));

                header->TableHash = XXHash64.Calculate(p.DataPointer + sizeof(long), DefaultDictionaryTableSize + PersistentDictionaryHeader.SizeOf - sizeof(long));

#if DEBUG
                VerifyTable(p);
#endif

                pageNumber = p.PageNumber;

                using var scope = llt.RootObjects.DirectAdd(defaultKey, sizeof(PersistentDictionaryRootHeader), out var ptr);
                *(PersistentDictionaryRootHeader*)ptr = new PersistentDictionaryRootHeader()
                {
                    RootObjectType = RootObjectType.PersistentDictionary,
                    PageNumber = pageNumber
                };
            }

            return pageNumber;
        }

        public static PersistentDictionary CreateIfBetter<TKeys1, TKeys2>(LowLevelTransaction llt, TKeys1 trainEnumerator, TKeys2 testEnumerator, PersistentDictionary previousDictionary = null)
            where TKeys1 : struct, IReadOnlySpanEnumerator
            where TKeys2 : struct, IReadOnlySpanEnumerator
        {
            var encoderState = new AdaptiveMemoryEncoderState(DefaultDictionaryTableSize);
            using var encoder = new HopeEncoder<Encoder3Gram<AdaptiveMemoryEncoderState>>(new Encoder3Gram<AdaptiveMemoryEncoderState>(encoderState));
            encoder.Train(trainEnumerator, MaxDictionaryEntries);                
            
            // Test the new dictionary to ensure that we have statistically better compression.
            using var encodeBufferScope = llt.Allocator.Allocate(Constants.Storage.PageSize, out var encodeBuffer);
            
            int incumbentSize = 0;
            int successorSize = 0;
            var auxEncodeBuffer = encodeBuffer.ToSpan();
            while (testEnumerator.MoveNext(out var testValue))
            {                
                incumbentSize += previousDictionary._encoder.Encode(testValue, auxEncodeBuffer);
                successorSize += encoder.Encode(testValue, auxEncodeBuffer);
            }

            // If the new dictionary is not at least 5% better, we return the current dictionary.             
            if (incumbentSize < successorSize * 1.05)
                return previousDictionary;

            int requiredSize = Encoder3Gram<AdaptiveMemoryEncoderState>.GetDictionarySize(encoderState);
            int requiredTotalSize = requiredSize + PersistentDictionaryHeader.SizeOf;
            var numberOfPages = VirtualPagerLegacyExtensions.GetNumberOfOverflowPages(requiredTotalSize);
            var p = llt.AllocatePage(numberOfPages);
            p.Flags = PageFlags.Overflow;
            p.OverflowSize = requiredTotalSize;           

            PersistentDictionaryHeader* header = (PersistentDictionaryHeader*)p.DataPointer;
            header->CurrentId = p.PageNumber;
            header->PreviousId = previousDictionary != null ? previousDictionary.PageNumber : 0;

            byte* encodingTablesPtr = p.DataPointer + PersistentDictionaryHeader.SizeOf;
            encoderState.EncodingTable.Slice(0, requiredSize / 2).CopyTo(new Span<byte>(encodingTablesPtr, requiredSize / 2));
            encoderState.DecodingTable.Slice(0, requiredSize / 2).CopyTo(new Span<byte>(encodingTablesPtr + requiredSize / 2, requiredSize / 2));

            var nativeState = new NativeMemoryEncoderState(encodingTablesPtr, requiredSize);
            header->TableSize = requiredSize;
            header->TableHash = XXHash64.Calculate(p.DataPointer + sizeof(ulong), (ulong) (header->TableSize + PersistentDictionaryHeader.SizeOf - sizeof(ulong)));

#if DEBUG
            VerifyTable(p);
#endif

            return new PersistentDictionary(p);
        }

        public static void VerifyTable(Page page)
        {
            PersistentDictionaryHeader* header = (PersistentDictionaryHeader*)page.DataPointer;

            // If checksum is not correct, we will throw a corruption exception
            var tableHash = XXHash64.Calculate(page.DataPointer + sizeof(long), (ulong)header->TableSize + PersistentDictionaryHeader.SizeOf - sizeof(long));
            if (tableHash != header->TableHash)
                throw new VoronErrorException($"Persistent storage checksum mismatch, expected: {tableHash}, actual: {header->TableHash}");

            // In the future for recovery scenarios we should do more advanced encoding/decoding verifications to ensure the table is not corrupted. 
            // https://issues.hibernatingrhinos.com/issue/RavenDB-18547
        }

        public PersistentDictionary(Page page)
        {
            _page = page;

            PersistentDictionaryHeader* header = (PersistentDictionaryHeader*)page.DataPointer;

            _encoder = new HopeEncoder<Encoder3Gram<NativeMemoryEncoderState>>(
                new Encoder3Gram<NativeMemoryEncoderState>(
                    new NativeMemoryEncoderState(page.DataPointer + PersistentDictionaryHeader.SizeOf, header->TableSize)));
        }

        public void Decode(ReadOnlySpan<byte> encodedKey, ref Span<byte> decodedKey)
        {
            int len = _encoder.Decode(encodedKey, decodedKey);
            decodedKey = decodedKey.Slice(0, len);
        }

        public void Encode(ReadOnlySpan<byte> key, ref Span<byte> encodedKey)
        {
            if (key.Length == 0)
                throw new ArgumentException("Cannot encode an empty key!", nameof(key));

            if (key[^1] != 0)
            {
                if (_tempBuffer == null || _tempBuffer.Length < key.Length + 1)
                {
                    if (_tempBuffer != null)
                        ArrayPool<byte>.Shared.Return(_tempBuffer);
                    _tempBuffer = ArrayPool<byte>.Shared.Rent(key.Length + 1);
                }

                var newKey = _tempBuffer.AsSpan();
                key.CopyTo(newKey);
                newKey[key.Length] = 0;
                key = newKey.Slice(0, key.Length + 1);
            }
            
            int bitsLength = _encoder.Encode(key, encodedKey);
            int bytesLength = Math.DivRem(bitsLength, 8, out var remainder);
            encodedKey = encodedKey.Slice(0, bytesLength + (remainder == 0 ? 0 : 1));
        }

        public int GetMaxEncodingBytes(ReadOnlySpan<byte> key)
        {
            // The plus one is because we may be sending non null terminated strings and we have to account for it. 
            return Math.Max(sizeof(long), _encoder.GetMaxEncodingBytes(key.Length + 1));
        }

        public int GetMaxDecodingBytes(ReadOnlySpan<byte> key)
        {
            return _encoder.GetMaxDecodingBytes(key.Length);
        }
    }
}
