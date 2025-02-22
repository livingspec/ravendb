﻿using System;
using System.Collections.Generic;
using Corax;
using Corax.Utils;
using Raven.Server.Documents.Indexes.Spatial;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Persistence.Corax.WriterScopes
{
    public interface IWriterScope
    {
        public void WriteNull(int field, ref IndexEntryWriter entryWriter);
        public void Write(int field, ReadOnlySpan<byte> value, ref IndexEntryWriter entryWriter);

        public void Write(int field, ReadOnlySpan<byte> value, long longValue, double doubleValue, ref IndexEntryWriter entryWriter);
        
        public void Write(int field, string value, ref IndexEntryWriter entryWriter);
        
        public void Write(int field, string value, long longValue, double doubleValue, ref IndexEntryWriter entryWriter);
        
        public void Write(int field, BlittableJsonReaderObject reader, ref IndexEntryWriter entryWriter);

        public void Write(int field, CoraxSpatialPointEntry entry, ref IndexEntryWriter entryWriter);
    }
}
