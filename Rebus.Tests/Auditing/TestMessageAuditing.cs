﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.Auditing.Messages;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Messages;
using Rebus.Tests.Contracts;
using Rebus.Tests.Contracts.Extensions;
using Rebus.Tests.Extensions;
using Rebus.Transport.InMem;
#pragma warning disable 1998

namespace Rebus.Tests.Auditing
{
    [TestFixture]
    public class TestMessageAuditing : FixtureBase
    {
        IBus _bus;
        BuiltinHandlerActivator _adapter;
        InMemNetwork _network;

        protected override void SetUp()
        {
            _adapter = new BuiltinHandlerActivator();

            Using(_adapter);

            _network = new InMemNetwork();
            
            _bus = Configure.With(_adapter)
                .Transport(t => t.UseInMemoryTransport(_network, "test"))
                .Options(o =>
                {
                    o.LogPipeline(true);
                    o.EnableMessageAuditing("audit");
                })
                .Start();
        }

        [Test]
        public async Task DoesNotCopyFailedMessage()
        {
            _adapter.Handle<string>(async _ =>
            {
                throw new Exception("w00t!!");
            });

            await _bus.SendLocal("woohooo!!!!");

            await Task.Delay(TimeSpan.FromSeconds(3));

            var message = _network.GetNextOrNull("audit");

            Assert.That(message, Is.Null, "Apparently, a message copy was received anyway!!");
        }

        [Test]
        public async Task CopiesProperlyHandledMessageToAuditQueue()
        {
            var gotTheMessage = new ManualResetEvent(false);

            _adapter.Handle<string>(async _ =>
            {
                gotTheMessage.Set();
            });

            await _bus.SendLocal("woohooo!!!!");

            gotTheMessage.WaitOrDie(TimeSpan.FromSeconds(5));

            var message = await _network.WaitForNextMessageFrom("audit");

            PrintHeaders(message);

            Assert.That(message.Headers.ContainsKey(AuditHeaders.AuditTime));
            Assert.That(message.Headers.ContainsKey(AuditHeaders.HandleTime));
            Assert.That(message.Headers.ContainsKey(Headers.Intent));
            Assert.That(message.Headers[Headers.Intent], Is.EqualTo(Headers.IntentOptions.PointToPoint));
        }

        [Test]
        public async Task CopiesPublishedMessageToAuditQueue()
        {
            await _bus.Advanced.Topics.Publish("TOPIC: 'whocares/nosubscribers'", "woohooo!!!!");

            var message = await _network.WaitForNextMessageFrom("audit");

            PrintHeaders(message);

            Assert.That(message.Headers.ContainsKey(AuditHeaders.AuditTime));
            Assert.That(message.Headers.ContainsKey(Headers.Intent));
            Assert.That(message.Headers[Headers.Intent], Is.EqualTo(Headers.IntentOptions.PublishSubscribe));
        }

        static void PrintHeaders(TransportMessage message)
        {
            Console.WriteLine(@"Headers:
{0}", string.Join(Environment.NewLine, message.Headers.Select(kvp => $"    {kvp.Key}: {kvp.Value}")));
        }
    }
}