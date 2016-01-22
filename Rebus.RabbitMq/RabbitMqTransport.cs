﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Rebus.Bus;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Subscriptions;
using Rebus.Transport;

#pragma warning disable 1998

namespace Rebus.RabbitMq
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses RabbitMQ to send/receive messages
    /// </summary>
    public class RabbitMqTransport : ITransport, IDisposable, IInitializable, ISubscriptionStorage
    {
        const string DirectExchangeName = "RebusDirect";
        const string TopicExchangeName = "RebusTopics";
        const string CurrentModelItemsKey = "rabbitmq-current-model";
        const string OutgoingMessagesItemsKey = "rabbitmq-outgoing-messages";

        static readonly Encoding HeaderValueEncoding = Encoding.UTF8;

        readonly ConnectionManager _connectionManager;
        readonly ILog _log;
        private readonly bool _exclusive;
        private readonly bool _durable;
        private readonly bool _autoDelete;

        /// <summary>
        /// Constructs the transport with a connection to the RabbitMQ instance specified by the given connection string
        /// </summary>
        public RabbitMqTransport(string connectionString, string inputQueueAddress, IRebusLoggerFactory rebusLoggerFactory, bool exclusive = false, bool durable = true, bool autoDelete = false)
        {
            _connectionManager = new ConnectionManager(connectionString, inputQueueAddress, rebusLoggerFactory);
            _log = rebusLoggerFactory.GetCurrentClassLogger();

            Address = inputQueueAddress;
            _durable = durable;
            _exclusive = exclusive;
            _autoDelete = autoDelete;
        }

        public void Initialize()
        {
            if (Address == null) return;

            CreateQueue(Address, _exclusive, _durable, _autoDelete);
        }

        public void CreateQueue(string address)
        {
            
            CreateQueue(address, false, true, false);
        }

        private void CreateQueue(string address, bool exclusive, bool durable, bool autoDelete)
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                model.ExchangeDeclare(DirectExchangeName, ExchangeType.Direct, true);
                model.ExchangeDeclare(TopicExchangeName, ExchangeType.Topic, true);

                var arguments = new Dictionary<string, object>
                {
                    {"x-ha-policy", "all"}
                };

                model.QueueDeclare(address, exclusive: exclusive, durable: durable, autoDelete: autoDelete, arguments: arguments);

                model.QueueBind(address, DirectExchangeName, address);
            }
        }

        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            var outgoingMessages = context.GetOrAdd(OutgoingMessagesItemsKey, () =>
            {
                var messages = new ConcurrentQueue<OutgoingMessage>();

                context.OnCommitted(() => SendOutgoingMessages(context, messages));

                return messages;
            });

            outgoingMessages.Enqueue(new OutgoingMessage(destinationAddress, message));
        }

        public string Address { get; private set; }

        /// <summary>
        /// Deletes all messages from the queue
        /// </summary>
        public void PurgeInputQueue()
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                try
                {
                    model.QueuePurge(Address);
                }
                catch (OperationInterruptedException exception)
                {
                    var shutdownReason = exception.ShutdownReason;

                    var queueDoesNotExist = shutdownReason != null
                                            && shutdownReason.ReplyCode == 404;

                    if (queueDoesNotExist)
                    {
                        return;
                    }

                    throw;
                }
            }
        }

        /// <summary>
        /// Sets max for how many messages the RabbitMQ driver should download in the background
        /// </summary>
        public void SetPrefetching(int value)
        {
        }

        public async Task<TransportMessage> Receive(ITransactionContext context)
        {
            if (Address == null)
            {
                throw new InvalidOperationException("This RabbitMQ transport does not have an input queue, hence it is not possible to reveive anything");
            }

            var model = GetModel(context);
            var result = model.BasicGet(Address, false);

            if (result == null) return null;

            var deliveryTag = result.DeliveryTag;

            context.OnCompleted(async () =>
            {
                model.BasicAck(deliveryTag, false);
            });

            context.OnAborted(() =>
            {
                model.BasicNack(deliveryTag, false, true);
            });

            var headers = result.BasicProperties.Headers
                .ToDictionary(kvp => kvp.Key, kvp =>
                {
                    var headerValue = kvp.Value;

                    if (headerValue is byte[])
                    {
                        var stringHeaderValue = HeaderValueEncoding.GetString((byte[])headerValue);

                        return stringHeaderValue;
                    }

                    return headerValue.ToString();
                });

            return new TransportMessage(headers, result.Body);
        }

        async Task SendOutgoingMessages(ITransactionContext context, ConcurrentQueue<OutgoingMessage> outgoingMessages)
        {
            var model = GetModel(context);

            foreach (var outgoingMessage in outgoingMessages)
            {
                var destinationAddress = outgoingMessage.DestinationAddress;
                var message = outgoingMessage.TransportMessage;
                var props = model.CreateBasicProperties();
                var headers = message.Headers;
                var timeToBeDelivered = GetTimeToBeReceivedOrNull(message);

                props.Headers = headers
                    .ToDictionary(kvp => kvp.Key, kvp => (object)HeaderValueEncoding.GetBytes(kvp.Value));

                if (timeToBeDelivered.HasValue)
                {
                    props.Expiration = timeToBeDelivered.Value.TotalMilliseconds.ToString("0");
                }

                var express = headers.ContainsKey(Headers.Express);

                props.Persistent = !express;

                var routingKey = new FullyQualifiedRoutingKey(destinationAddress);

                model.BasicPublish(routingKey.ExchangeName, routingKey.RoutingKey, props, message.Body);
            }
        }

        class FullyQualifiedRoutingKey
        {
            public FullyQualifiedRoutingKey(string destinationAddress)
            {
                if (destinationAddress == null) throw new ArgumentNullException("destinationAddress");

                var tokens = destinationAddress.Split('@');

                if (tokens.Length > 1)
                {
                    ExchangeName = tokens.Last();
                    RoutingKey = string.Join("@", tokens.Take(tokens.Length - 1));
                }
                else
                {
                    ExchangeName = DirectExchangeName;
                    RoutingKey = destinationAddress;
                }
            }

            public string ExchangeName { get; private set; }
            public string RoutingKey { get; private set; }
        }

        static TimeSpan? GetTimeToBeReceivedOrNull(TransportMessage message)
        {
            var headers = message.Headers;
            TimeSpan? timeToBeReceived = null;
            if (headers.ContainsKey(Headers.TimeToBeReceived))
            {
                var timeToBeReceivedStr = headers[Headers.TimeToBeReceived];
                timeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);
            }
            return timeToBeReceived;
        }

        IModel GetModel(ITransactionContext context)
        {
            return context.GetOrAdd(CurrentModelItemsKey, () =>
            {
                var connection = _connectionManager.GetConnection();
                var newModel = connection.CreateModel();

                context.OnDisposed(() => newModel.Dispose());

                return newModel;
            });
        }

        class OutgoingMessage
        {
            public OutgoingMessage(string destinationAddress, TransportMessage transportMessage)
            {
                DestinationAddress = destinationAddress;
                TransportMessage = transportMessage;
            }

            public string DestinationAddress { get; private set; }

            public TransportMessage TransportMessage { get; private set; }
        }

        public void Dispose()
        {
            _connectionManager.Dispose();
        }

        public async Task<string[]> GetSubscriberAddresses(string topic)
        {
            return new[] { string.Format("{0}@{1}", topic, TopicExchangeName) };
        }

        public async Task RegisterSubscriber(string topic, string subscriberAddress)
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                model.QueueBind(Address, TopicExchangeName, topic);
            }
        }

        public async Task UnregisterSubscriber(string topic, string subscriberAddress)
        {
            var connection = _connectionManager.GetConnection();

            using (var model = connection.CreateModel())
            {
                model.QueueUnbind(Address, TopicExchangeName, topic, new Dictionary<string, object>());
            }
        }

        public bool IsCentralized
        {
            get { return true; }
        }
    }
}