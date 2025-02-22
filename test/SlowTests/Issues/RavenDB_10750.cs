﻿using System.Linq;
using FastTests;
using Raven.Client.Documents.Commands.Batches;
using Raven.Client.Documents.Indexes;
using Raven.Tests.Core.Utils.Entities;
using Sparrow.Json;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues
{
    public class RavenDB_10750:RavenTestBase
    {
        public RavenDB_10750(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax, Skip = "RavenDB-17966")]
        public void ShouldNotCreatePropertyAfterAccessingIt(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                using (var session = store.OpenSession())
                {
                    session.Store(new User (),"users/1");
                    session.SaveChanges();

                    session.Advanced.Defer(new PatchCommandData("users/1", null, new Raven.Client.Documents.Operations.PatchRequest
                    {
                        Script = @"
                        if (this.NonExistingField != null)
                            this.NonExistingField = []; "
                    }, null));

                    session.SaveChanges();

                    var results = store.Commands().Query(new Raven.Client.Documents.Queries.IndexQuery()
                    {
                        Query = "from users",
                        WaitForNonStaleResults =true
                    });

                    var firstResult = results.Results[0] as BlittableJsonReaderObject;

                    Assert.Equal(-1, firstResult.GetPropertyIndex("NonExistingField"));


                }                
            }
        }

        public class UsersIndex : AbstractIndexCreationTask<User>
        {
            public UsersIndex()
            {
                Map = users=> from user in users                 
                select new
                {
                    user.Name,
                    user.Age,
                    _ = CreateField("newField", "newValue", true, false)
                };
                Index(x => x.Name, FieldIndexing.Exact);
                Store(x => x.Name, FieldStorage.Yes);                
            }
        }

        [Theory]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax, Skip = "RavenDB-17966")]
        public void ShouldNotProjectPropertyValueIfOnlyViewedStoredIndexedValue(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                new UsersIndex().Execute(store);
                using (var session = store.OpenSession())
                {
                    session.Store(new User(), "users/1");
                    
                    session.SaveChanges();

                    var results = store.Commands().Query(new Raven.Client.Documents.Queries.IndexQuery()
                    {
                        Query = @"
from index 'UsersIndex' as u 
select {
a:u,
b:u.newField
}
",
                        WaitForNonStaleResults = true                        
                    });

                    var firstResult = results.Results[0] as BlittableJsonReaderObject;

                    Assert.Equal(-1, (firstResult["a"] as BlittableJsonReaderObject).GetPropertyIndex("newField"));
                    Assert.Equal("newValue", firstResult["b"].ToString());


                }
            }
        }


        [Theory]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax, Skip = "RavenDB-17966")]
        public void ShouldProjectPropertyValueIfViewedAndUpdatedStoredIndexedValue(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                new UsersIndex().Execute(store);
                using (var session = store.OpenSession())
                {
                    session.Store(new User(), "users/1");

                    session.SaveChanges();

                    var results = store.Commands().Query(new Raven.Client.Documents.Queries.IndexQuery()
                    {
                        Query = @"
declare function proj(doc){
doc.newField = doc.newField + '2';
return doc
}

from index 'UsersIndex' as u 
select {
a:proj(u),
b:u.newField
}
",
                        WaitForNonStaleResults = true
                    });

                    var firstResult = results.Results[0] as BlittableJsonReaderObject;

                    Assert.Equal("newValue2", (firstResult["a"] as BlittableJsonReaderObject)["newField"].ToString());
                    Assert.Equal("newValue2", firstResult["b"].ToString());
                }
            }
        }


        [Theory]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Lucene)]
        [RavenData(SearchEngineMode = RavenSearchEngineMode.Corax, Skip = "RavenDB-17966")]
        public void ShouldProjectPropertyValueIfViewedAndUpdatedStoredIndexedValueShouldRespectOperationsOrder(Options options)
        {
            using (var store = GetDocumentStore(options))
            {
                new UsersIndex().Execute(store);
                using (var session = store.OpenSession())
                {
                    session.Store(new User(), "users/1");

                    session.SaveChanges();

                    var results = store.Commands().Query(new Raven.Client.Documents.Queries.IndexQuery()
                    {
                        Query = @"
declare function proj(doc){
doc.newField = doc.newField + '2';
return doc
}

from index 'UsersIndex' as u 
select {
b:u.newField,
a:proj(u)
}
",
                        WaitForNonStaleResults = true
                    });

                    var firstResult = results.Results[0] as BlittableJsonReaderObject;

                    Assert.Equal("newValue2", (firstResult["a"] as BlittableJsonReaderObject)["newField"].ToString());
                    Assert.Equal("newValue", firstResult["b"].ToString());



                }
            }
        }
    }
}
