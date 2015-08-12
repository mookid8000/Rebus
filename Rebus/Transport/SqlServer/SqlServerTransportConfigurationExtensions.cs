﻿using Rebus.Config;
using Rebus.Persistence.SqlServer;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Timeouts;

namespace Rebus.Transport.SqlServer
{
    /// <summary>
    /// Configuration extensions for the SQL transport
    /// </summary>
    public static class SqlServerTransportConfigurationExtensions
    {
        /// <summary>
        /// Configures Rebus to use SQL Server as its transport. The table specified by <paramref name="tableName"/> will be used to
        /// store messages, and the "queue" specified by <paramref name="inputQueueName"/> will be used when querying for messages.
        /// The message table will automatically be created if it does not exist.
        /// </summary>
        public static void UseSqlServer(this StandardConfigurer<ITransport> configurer, string connectionStringOrConnectionStringName, string tableName, string inputQueueName)
        {
            configurer.Register(context =>
            {
                var connectionProvider = new DbConnectionProvider(connectionStringOrConnectionStringName);
                var transport = new SqlServerTransport(connectionProvider, tableName, inputQueueName);
                transport.EnsureTableIsCreated();
                return transport;
            });

            configurer.OtherService<ITimeoutManager>().Register(c => new DisabledTimeoutManager());

            configurer.OtherService<IPipeline>().Decorate(c =>
            {
                var pipeline = c.Get<IPipeline>();

                return new PipelineStepRemover(pipeline)
                    .RemoveIncomingStep(s => s.GetType() == typeof (HandleDeferredMessagesStep));
            });
        }
    }
}