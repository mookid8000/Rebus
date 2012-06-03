﻿using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Rebus.Bus;
using Rebus.Logging;
using Rebus.Persistence.InMemory;
using Rebus.Timeout;
using Shouldly;

namespace Rebus.Tests.Integration
{
    [TestFixture, Category(TestCategories.Integration)]
    public class TestMessageDeferral : RebusBusMsmqIntegrationTestBase
    {
        RebusBus bus;
        TimeoutService timeoutService;
        HandlerActivatorForTesting handlerActivator;

        protected override void DoSetUp()
        {
            handlerActivator = new HandlerActivatorForTesting();
            bus = CreateBus("test.deferral", handlerActivator).Start(1);
            timeoutService = new TimeoutService(new InMemoryTimeoutStorage());
            timeoutService.Start();

            RebusLoggerFactory.Current = new ConsoleLoggerFactory(false) { MinLevel = LogLevel.Warn };
        }

        protected override void DoTearDown()
        {
            timeoutService.Stop();
        }

        [Test]
        public void CanMakeSimpleDeferralOfMessages()
        {
            // arrange
            var messages = new List<Tuple<string, DateTime>>();
            handlerActivator.Handle<MessageWithText>(m => messages.Add(new Tuple<string, DateTime>(m.Text, DateTime.UtcNow)));

            var timeOfDeferral = DateTime.UtcNow;
            var acceptedTolerance = 2.Seconds();

            // act
            bus.Defer(10.Seconds(), new MessageWithText {Text = "deferred 10 seconds"});
            bus.Defer(5.Seconds(), new MessageWithText {Text = "deferred 5 seconds"});

            Thread.Sleep(10.Seconds() + acceptedTolerance + acceptedTolerance);

            // assert
            messages.Count.ShouldBe(2);
            messages[0].Item1.ShouldBe("deferred 5 seconds");
            messages[0].Item2.ElapsedSince(timeOfDeferral).ShouldBeGreaterThan(5.Seconds() - acceptedTolerance);
            messages[0].Item2.ElapsedSince(timeOfDeferral).ShouldBeLessThan(5.Seconds() + acceptedTolerance);

            messages[1].Item1.ShouldBe("deferred 10 seconds");
            messages[1].Item2.ElapsedSince(timeOfDeferral).ShouldBeGreaterThan(10.Seconds() - acceptedTolerance);
            messages[1].Item2.ElapsedSince(timeOfDeferral).ShouldBeLessThan(10.Seconds() + acceptedTolerance);
        }

        class MessageWithText
        {
            public string Text { get; set; }
        }

        [Test, Ignore("takes a while")]
        public void WorksReliablyWithMoreTimeouts()
        {
            // arrange
            var messages = new List<Tuple<DateTime, DateTime>>();
            handlerActivator.Handle<MessageWithExpectedReturnTime>(m => messages.Add(new Tuple<DateTime, DateTime>(m.ExpectedReturnTime, DateTime.UtcNow)));

            var acceptedTolerance = 2.Seconds();
            var random = new Random();

            // act
            500.Times(() =>
                          {
                              var delay = (random.Next(20) + 10).Seconds();
                              var message = new MessageWithExpectedReturnTime {ExpectedReturnTime = DateTime.UtcNow + delay};
                              bus.Defer(delay, message);
                          });

            Thread.Sleep(30.Seconds() + acceptedTolerance + acceptedTolerance);

            // assert
            messages.Count.ShouldBe(500);

            foreach(var messageTimes in messages)
            {
                messageTimes.Item2.ShouldBeGreaterThan(messageTimes.Item1 - acceptedTolerance);
                messageTimes.Item2.ShouldBeLessThan(messageTimes.Item1 + acceptedTolerance);
            }
        }

        class MessageWithExpectedReturnTime
        {
            public DateTime ExpectedReturnTime { get; set; }
        }
    }
}