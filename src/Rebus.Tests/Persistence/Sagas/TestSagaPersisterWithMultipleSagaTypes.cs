﻿using System;
using System.Linq;
using NUnit.Framework;
using Ponder;
using Rebus.Tests.Persistence.Sagas.Factories;
using Shouldly;

namespace Rebus.Tests.Persistence.Sagas
{
    [TestFixture(typeof(MongoDbSagaPersisterFactory), Category = TestCategories.Mongo)]
    [TestFixture(typeof(SqlServerSagaPersisterFactory), Category = TestCategories.MsSql)]
    [TestFixture(typeof(RavenDbSagaPersisterFactory), Category = TestCategories.Raven)]
    public class TestSagaPersisterWithMultipleSagaTypes<TFactory> : TestSagaPersistersBase<TFactory> where TFactory : ISagaPersisterFactory
    {
        [Test]
        public void CanInsertSagasOfMultipleTypes()
        {
            // arrange
            var someString = "just happens to be the same in two otherwise unrelated sagas";
            var someFieldPathOne = Reflect.Path<OneKindOfSaga>(s => s.SomeField);
            var someFieldPathAnother = Reflect.Path<AnotherKindOfSaga>(s => s.SomeField);

            // act
            Persister.Insert(new OneKindOfSaga { Id = Guid.NewGuid(), SomeField = someString }, new[] { "Id", someFieldPathOne });
            Persister.Insert(new AnotherKindOfSaga { Id = Guid.NewGuid(), SomeField = someString }, new[] { "Id", someFieldPathAnother });

            var oneKindOfSagaLoaded = Persister.Find<OneKindOfSaga>(someFieldPathOne, someString).Single();
            var anotherKindOfSagaLoaded = Persister.Find<AnotherKindOfSaga>(someFieldPathAnother, someString).Single();

            // assert
            oneKindOfSagaLoaded.ShouldBeTypeOf<OneKindOfSaga>();
            anotherKindOfSagaLoaded.ShouldBeTypeOf<AnotherKindOfSaga>();
        }

        class OneKindOfSaga : ISagaData
        {
            public Guid Id { get; set; }
            public int Revision { get; set; }

            public string SomeField { get; set; }
        }

        class AnotherKindOfSaga : ISagaData
        {
            public Guid Id { get; set; }
            public int Revision { get; set; }

            public string SomeField { get; set; }
        }
    }
}