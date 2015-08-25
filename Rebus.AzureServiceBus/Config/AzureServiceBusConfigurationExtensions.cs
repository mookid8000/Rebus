﻿using System.Configuration;
using Rebus.AzureServiceBus;
using Rebus.AzureServiceBus.Config;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Subscriptions;
using Rebus.Timeouts;
using Rebus.Transport;

// ReSharper disable once CheckNamespace
namespace Rebus.Config
{
    /// <summary>
    /// Configuration extensions for the Azure Service Bus transport
    /// </summary>
    public static class AzureServiceBusConfigurationExtensions
    {
        /// <summary>
        /// Configures Rebus to use Azure Service Bus to transport messages as a one-way client (i.e. will not be able to receive any messages)
        /// </summary>
        public static void UseAzureServiceBusAsOneWayClient(this StandardConfigurer<ITransport> configurer, string connectionStringNameOrConnectionString, AzureServiceBusMode mode = AzureServiceBusMode.Standard)
        {
            var connectionString = GetConnectionString(connectionStringNameOrConnectionString);

            if (mode == AzureServiceBusMode.Basic)
            {
                configurer.Register(c => new BasicAzureServiceBusTransport(connectionString, null));
                OneWayClientBackdoor.ConfigureOneWayClient(configurer);
                return;
            }
           
            configurer
                .OtherService<AzureServiceBusTransport>()
                .Register(c => new AzureServiceBusTransport(connectionString, null));

            configurer
                .OtherService<ISubscriptionStorage>()
                .Register(c => c.Get<AzureServiceBusTransport>());

            configurer.Register(c => c.Get<AzureServiceBusTransport>());

            configurer.OtherService<ITimeoutManager>().Register(c => new DisabledTimeoutManager());

            OneWayClientBackdoor.ConfigureOneWayClient(configurer);
        }

        /// <summary>
        /// Configures Rebus to use Azure Service Bus queues to transport messages, connecting to the service bus instance pointed to by the connection string
        /// (or the connection string with the specified name from the current app.config)
        /// </summary>
        public static AzureServiceBusTransportSettings UseAzureServiceBus(this StandardConfigurer<ITransport> configurer, string connectionStringNameOrConnectionString, string inputQueueAddress, AzureServiceBusMode mode = AzureServiceBusMode.Standard)
        {
            var connectionString = GetConnectionString(connectionStringNameOrConnectionString);
            var settingsBuilder = new AzureServiceBusTransportSettings();

            if (mode == AzureServiceBusMode.Basic)
            {
                configurer.Register(c =>
                {
                    var transport = new BasicAzureServiceBusTransport(connectionString, inputQueueAddress);

                    if (settingsBuilder.PrefetchingEnabled)
                    {
                        transport.PrefetchMessages(settingsBuilder.NumberOfMessagesToPrefetch);
                    }

                    if (settingsBuilder.AutomaticPeekLockRenewalEnabled)
                    {
                        transport.AutomaticallyRenewPeekLock();
                    }

                    return transport;
                });

                return settingsBuilder;
            }

            configurer
                .OtherService<AzureServiceBusTransport>()
                .Register(c =>
                {
                    var transport = new AzureServiceBusTransport(connectionString, inputQueueAddress);

                    if (settingsBuilder.PrefetchingEnabled)
                    {
                        transport.PrefetchMessages(settingsBuilder.NumberOfMessagesToPrefetch);
                    }

                    if (settingsBuilder.AutomaticPeekLockRenewalEnabled)
                    {
                        transport.AutomaticallyRenewPeekLock();
                    }

                    return transport;
                });

            configurer
                .OtherService<ISubscriptionStorage>()
                .Register(c => c.Get<AzureServiceBusTransport>());

            configurer.Register(c => c.Get<AzureServiceBusTransport>());

            configurer.OtherService<IPipeline>().Decorate(c =>
            {
                var pipeline = c.Get<IPipeline>();

                return new PipelineStepRemover(pipeline)
                    .RemoveIncomingStep(s => s.GetType() == typeof(HandleDeferredMessagesStep));
            });

            configurer.OtherService<ITimeoutManager>().Register(c => new DisabledTimeoutManager());

            return settingsBuilder;
        }

        static string GetConnectionString(string connectionStringNameOrConnectionString)
        {
            var connectionStringSettings = ConfigurationManager.ConnectionStrings[connectionStringNameOrConnectionString];

            if (connectionStringSettings == null)
            {
                return connectionStringNameOrConnectionString;
            }

            return connectionStringNameOrConnectionString;
        }
    }
}