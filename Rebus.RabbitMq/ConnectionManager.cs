﻿using System;
using System.Collections.Generic;
using RabbitMQ.Client;
using Rebus.Logging;

namespace Rebus.RabbitMq
{
    class ConnectionManager : IDisposable
    {
        static ILog _log;

        static ConnectionManager()
        {
            RebusLoggerFactory.Changed += f => _log = f.GetCurrentClassLogger();
        }

        readonly object _activeConnectionLock = new object();
        readonly ConnectionFactory _connectionFactory;

        IConnection _activeConnection;

        public ConnectionManager(string connectionString, string inputQueueAddress)
        {
            if (inputQueueAddress != null)
            {
                _log.Info("Initializing RabbitMQ connection manager for transport with input queue '{0}'", inputQueueAddress);
            }
            else
            {
                _log.Info("Initializing RabbitMQ connection manager for one-way transport");
            }

            _connectionFactory = new ConnectionFactory
            {
                Uri = connectionString,
                ClientProperties = CreateClientProperties(inputQueueAddress),
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(50),
            };
        }

        IDictionary<string, object> CreateClientProperties(string inputQueueAddress)
        {
            return new Dictionary<string, object>
            {
                {"Machine", Environment.MachineName},
                {"InputQueue", inputQueueAddress ?? "<one-way client>"},
                {"Domain", Environment.UserDomainName},
                {"User", Environment.UserName},
            };
        }

        public IConnection GetConnection()
        {
            var connection = _activeConnection;

            if (connection != null) return connection;

            lock (_activeConnectionLock)
            {
                connection = _activeConnection;

                if (connection != null) return connection;

                _log.Info("Creating new RabbitMQ connection");
                _activeConnection = _connectionFactory.CreateConnection();

                return _activeConnection;
            }
        }

        public void Dispose()
        {
            lock (_activeConnectionLock)
            {
                var connection = _activeConnection;

                if (connection != null)
                {
                    _log.Info("Disposing RabbitMQ connection");

                    connection.Dispose();
                    _activeConnection = null;
                }
            }
        }
    }
}