﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Transactions;
using NUnit.Framework;
using Rebus.Logging;
using Rebus.Tests.Transports.Rabbit;

namespace Rebus.Tests.Integration
{
    [TestFixture, Category(TestCategories.Rabbit)]
    public class TestRebusOnRabbit : RabbitMqFixtureBase
    {
        protected override void DoSetUp()
        {
            RebusLoggerFactory.Current = new ConsoleLoggerFactory(false) {MinLevel = LogLevel.Warn};
        }

        [TestCase(1, 1)]
        [TestCase(100, 3)]
        [TestCase(10000, 3, Ignore = TestCategories.IgnoreLongRunningTests)]
        [TestCase(10000, 5, Ignore = TestCategories.IgnoreLongRunningTests)]
        [TestCase(100000, 3, Ignore = TestCategories.IgnoreLongRunningTests)]
        [TestCase(100000, 5, Ignore = TestCategories.IgnoreLongRunningTests)]
        public void CanSendAndReceiveMessages(int messageCount, int numberOfWorkers)
        {
            const string senderQueueName = "test.rabbit.sender";
            const string receiverQueueName = "test.rabbit.receiver";

            var receivedMessages = new ConcurrentBag<string>();

            var resetEvent = new ManualResetEvent(false);
            
            var sender = CreateBus(senderQueueName, new HandlerActivatorForTesting()).Start(1);

            var receiver = CreateBus(receiverQueueName,
                                     new HandlerActivatorForTesting()
                                         .Handle<string>(str =>
                                                             {
                                                                 receivedMessages.Add(str);
                                                                 
                                                                 if (receivedMessages.Count == messageCount)
                                                                 {
                                                                     resetEvent.Set();
                                                                 }
                                                             }));

            var stopwatch = Stopwatch.StartNew();
            using (var tx = new TransactionScope())
            {
                var counter = 0;
                
                messageCount.Times(() => sender.Send(receiverQueueName, "message #" + (counter++).ToString()));

                tx.Complete();
            }
            var totalSeconds = stopwatch.Elapsed.TotalSeconds;
            Console.WriteLine("Sending {0} messages took {1:0.0} s - that's {2:0} msg/s",
                              messageCount, totalSeconds, messageCount/totalSeconds);

            stopwatch = Stopwatch.StartNew();
            receiver.Start(numberOfWorkers);

            var accountForLatency = TimeSpan.FromSeconds(1);
            if (!resetEvent.WaitOne(TimeSpan.FromSeconds(messageCount*0.01) + accountForLatency))
            {
                Assert.Fail("Didn't receive all messages within timeout");
            }
            totalSeconds = stopwatch.Elapsed.TotalSeconds;

            Console.WriteLine("Receiving {0} messages took {1:0.0} s - that's {2:0} msg/s",
                              messageCount, totalSeconds, messageCount / totalSeconds);
        }
    }
}