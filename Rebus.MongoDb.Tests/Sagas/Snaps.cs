﻿using NUnit.Framework;
using Rebus.Tests.Contracts.Sagas;

namespace Rebus.MongoDb.Tests.Sagas
{
    [TestFixture]
    public class MongoDbSagaSnapshotStorageTest : SagaSnapshotStorageTest<MongoDbSnapshotStorageFactory>
    {
    }
}