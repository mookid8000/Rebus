using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rebus.Activation;
using Rebus.AzureServiceBus.Config;
using Rebus.Bus;
using Rebus.Config;
using Rebus.Tests;
using Rebus.Tests.Integration.ManyMessages;

namespace Rebus.AzureServiceBus.Tests.Factories
{
    public abstract class AzureServiceBusBusFactory : IBusFactory
    {
        readonly AzureServiceBusMode _mode;

        protected AzureServiceBusBusFactory(AzureServiceBusMode mode)
        {
            _mode = mode;
        }

        readonly List<IDisposable> _stuffToDispose = new List<IDisposable>();

        public IBus GetBus<TMessage>(string inputQueueAddress, Func<TMessage, Task> handler)
        {
            var builtinHandlerActivator = new BuiltinHandlerActivator();

            builtinHandlerActivator.Handle(handler);

            var queueName = TestConfig.QueueName(inputQueueAddress);

            PurgeQueue(queueName);

            var bus = Configure.With(builtinHandlerActivator)
                .Transport(t => t.UseAzureServiceBus(StandardAzureServiceBusTransportFactory.ConnectionString, queueName, _mode))
                .Options(o =>
                {
                    o.SetNumberOfWorkers(10);
                    o.SetMaxParallelism(10);
                })
                .Start();

            _stuffToDispose.Add(bus);

            return bus;
        }

        static void PurgeQueue(string queueName)
        {
            new AzureServiceBusTransport(StandardAzureServiceBusTransportFactory.ConnectionString, queueName)
                .PurgeInputQueue();
        }

        public void Cleanup()
        {
            _stuffToDispose.ForEach(d => d.Dispose());
            _stuffToDispose.Clear();
        }
    }
}