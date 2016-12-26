﻿using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Bus.Advanced;
using Rebus.Config;
using Rebus.Handlers;
using Rebus.Pipeline;
using Rebus.Transport.InMem;
#pragma warning disable 1998

namespace Rebus.Tests.Contracts.Activation
{
    public class RealContainerTests<TFactory> : FixtureBase where TFactory : IContainerAdapterFactory, new()
    {
        TFactory _factory;

        protected override void SetUp()
        {
            _factory = new TFactory();
        }

        [Test]
        public async Task CanInjectSyncBus()
        {
            HandlerThatGetsSyncBusInjected.SyncBusWasInjected = false;
            _factory.RegisterHandlerType<HandlerThatGetsSyncBusInjected>();

            var activator = _factory.GetActivator();

            var bus = Configure.With(activator)
                .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "hvassåbimse"))
                .Start();

            Using(bus);

            await bus.SendLocal("hej");

            await Task.Delay(500);

            Assert.That(HandlerThatGetsSyncBusInjected.SyncBusWasInjected, Is.True,
                "HandlerThatGetsSyncBusInjected did not get invoked properly with an injected ISyncBus");
        }

        [Test]
        public async Task CanInjectMessageContext()
        {
            HandlerThatGetsMessageContextInjected.MessageContextWasInjected = false;
            _factory.RegisterHandlerType<HandlerThatGetsMessageContextInjected>();

            var activator = _factory.GetActivator();

            var bus = Configure.With(activator)
                .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "hvassåbimse"))
                .Start();

            Using(bus);

            await bus.SendLocal("hej");

            await Task.Delay(500);

            Assert.That(HandlerThatGetsMessageContextInjected.MessageContextWasInjected, Is.True,
                "HandlerThatGetsMessageContextInjected did not get invoked properly with an injected IMessageContext");
        }

        class HandlerThatGetsMessageContextInjected : IHandleMessages<string>
        {
            public static bool MessageContextWasInjected;

            readonly IMessageContext _messageContext;

            public HandlerThatGetsMessageContextInjected(IMessageContext messageContext)
            {
                _messageContext = messageContext;
            }

            public async Task Handle(string message)
            {
                if (_messageContext != null)
                {
                    MessageContextWasInjected = true;
                }
            }
        }

        class HandlerThatGetsSyncBusInjected : IHandleMessages<string>
        {
            public static bool SyncBusWasInjected;

            readonly ISyncBus _syncBus;

            public HandlerThatGetsSyncBusInjected(ISyncBus syncBus)
            {
                _syncBus = syncBus;
            }

            public async Task Handle(string message)
            {
                if (_syncBus != null)
                {
                    SyncBusWasInjected = true;
                }
            }
        }
    }
}