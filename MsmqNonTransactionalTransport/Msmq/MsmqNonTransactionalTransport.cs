﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Messaging;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Rebus.Bus;
using Rebus.Extensions;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Transport;
using Message = System.Messaging.Message;
#pragma warning disable 1998

namespace MsmqNonTransactionalTransport.Msmq
{
    /// <summary>
    /// Implementation of <see cref="ITransport"/> that uses non transactional MSMQ to do its thing
    /// </summary>
    public class MsmqNonTransactionalTransport : ITransport, IInitializable, IDisposable
    {
        const string CurrentOutgoingQueuesKey = "msmqtransport-outgoing-messagequeues";
        readonly ExtensionSerializer _extensionSerializer = new ExtensionSerializer();
        readonly string _inputQueueName;
        readonly ILog _log;

        volatile MessageQueue _inputQueue;
        bool _disposed;

        /// <summary>
        /// Constructs the non transactional transport with the specified input queue address
        /// </summary>
        public MsmqNonTransactionalTransport(string inputQueueAddress, IRebusLoggerFactory rebusLoggerFactory)
        {
            if (rebusLoggerFactory == null) throw new ArgumentNullException(nameof(rebusLoggerFactory));
            
            _log = rebusLoggerFactory.GetCurrentClassLogger();

            if(inputQueueAddress != null)
            {
                _inputQueueName = MakeGloballyAddressable(inputQueueAddress);
            }
        }

        /// <summary>
        /// Last-resort disposal of the transport's message queues
        /// </summary>
        ~MsmqNonTransactionalTransport()
        {
            Dispose(false);
        }

        static string MakeGloballyAddressable(string inputQueueName)
        {
            return inputQueueName.Contains("@")
                ? inputQueueName
                : $"{inputQueueName}@{Environment.MachineName}";
        }

        /// <summary>
        /// Initializes the transport by creating the input queue
        /// </summary>
        public void Initialize()
        {
            if (_inputQueueName != null)
            {
                _log.Info("Initializing MSMQ transport - input queue: '{0}'", _inputQueueName);

                GetInputQueue();
            }
            else
            {
                _log.Info("Initializing one-way MSMQ transport");
            }
        }

        /// <summary>
        /// Creates a queue with the given address, unless the address is of a remote queue - in that case,
        /// this call is ignored
        /// </summary>
        public void CreateQueue(string address)
        {
            if (!MsmqUtil.IsLocal(address)) return;

            var inputQueuePath = MsmqUtil.GetPath(address);

            MsmqUtil.EnsureQueueExists(inputQueuePath, _log, false);
        }

        /// <summary>
        /// Deletes all messages in the input queue
        /// </summary>
        public void PurgeInputQueue()
        {
            if (!MsmqUtil.QueueExists(_inputQueueName))
            {
                _log.Info("Purging {0} (but the queue doesn't exist...)", _inputQueueName);
                return;
            }

            _log.Info("Purging {0}", _inputQueueName);
            MsmqUtil.PurgeQueue(_inputQueueName);
        }

        /// <summary>
        /// Sends the given transport message to the specified destination address using non transactional MSMQ.
        /// </summary>
        public async Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
        {
            if (destinationAddress == null) throw new ArgumentNullException(nameof(destinationAddress));
            if (message == null) throw new ArgumentNullException(nameof(message));
            if (context == null) throw new ArgumentNullException(nameof(context));

            var logicalMessage = CreateMsmqMessage(message);
            var sendQueues = context.GetOrAdd(CurrentOutgoingQueuesKey, () =>
            {
                var messageQueues = new ConcurrentDictionary<string, MessageQueue>(StringComparer.InvariantCultureIgnoreCase);

                context.OnDisposed(() =>
                {
                    foreach (var messageQueue in messageQueues.Values)
                    {
                        messageQueue.Dispose();
                    }
                });

                return messageQueues;
            });

            var path = MsmqUtil.GetFullPath(destinationAddress);

            var sendQueue = sendQueues.GetOrAdd(path, _ =>
            {
                var messageQueue = new MessageQueue(path, QueueAccessMode.Send);
                
                return messageQueue;
            });

            sendQueue.Send(logicalMessage);
        }

        /// <summary>
        /// Received the next available transport message from the input queue via MSMQ.
        /// TODO: context is not used here anymore, but still required by the interface.
        /// </summary>
        public async Task<TransportMessage> Receive(ITransactionContext context)
        {
            if (_inputQueueName == null)
            {
                throw new InvalidOperationException("This MSMQ transport does not have an input queue, hence it is not possible to reveive anything");
            }

            var queue = GetInputQueue();
            
            try
            {
                var message = queue.Receive(TimeSpan.FromSeconds(2));
                if (message == null)
                {
                    return null;
                }

                var headers = _extensionSerializer.Deserialize(message.Extension, message.Id);
                var body = new byte[message.BodyStream.Length];

                await message.BodyStream.ReadAsync(body, 0, body.Length);

                return new TransportMessage(headers, body);
            }
            catch (MessageQueueException exception)
            {
                if (exception.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
                {
                    return null;
                }

                if (exception.MessageQueueErrorCode == MessageQueueErrorCode.InvalidHandle)
                {
                    _log.Info("Queue handle for '{0}' was invalid - will try to reinitialize the queue", _inputQueueName);
                    ReinitializeInputQueue();
                }

                if (exception.MessageQueueErrorCode == MessageQueueErrorCode.QueueDeleted)
                {
                    _log.Warn("Queue '{0}' was deleted - will not receive any more messages", _inputQueueName);
                    return null;
                }

                throw new IOException($"Could not receive next message from MSMQ queue '{_inputQueueName}'", exception);
            }
        }

        Message CreateMsmqMessage(TransportMessage message)
        {
            var headers = message.Headers;

            var expressDelivery = headers.ContainsKey(Headers.Express);

            string timeToBeReceivedStr;
            var hasTimeout = headers.TryGetValue(Headers.TimeToBeReceived, out timeToBeReceivedStr);
            string priority;

            // set prio to normal as default?
            var msgPriority = MessagePriority.Normal;
            if(headers.TryGetValue(Headers.Priority, out priority))
            {
                msgPriority = (MessagePriority) Enum.Parse(typeof (MessagePriority), priority);
            }

            var msmqMessage = new Message
            {
                Extension = _extensionSerializer.Serialize(headers),
                BodyStream = new MemoryStream(message.Body),
                UseJournalQueue = false,
                Recoverable = !expressDelivery, 
                UseDeadLetterQueue = !(expressDelivery || hasTimeout),
                Label = GetMessageLabel(message),
                Priority = msgPriority
            };

            if (hasTimeout)
            {
                msmqMessage.TimeToBeReceived = TimeSpan.Parse(timeToBeReceivedStr);
            }

            return msmqMessage;
        }

        static string GetMessageLabel(TransportMessage message)
        {
            try
            {
                return message.GetMessageLabel();
            }
            catch
            {
                // if that failed, it's most likely because we're running in legacy mode - therefore:
                return message.Headers.GetValueOrNull(Headers.MessageId)
                       ?? message.Headers.GetValueOrNull("rebus-msg-id")
                       ?? "<unknown ID>";
            }
        }

        /// <summary>
        /// Gets the input queue address of this MSMQ queue
        /// </summary>
        public string Address => _inputQueueName;

        void ReinitializeInputQueue()
        {
            if (_inputQueue != null)
            {
                try
                {
                    _inputQueue.Close();
                    _inputQueue.Dispose();
                }
                catch (Exception exception)
                {
                    _log.Warn("An error occurred when closing/disposing the queue handle for '{0}': {1}", _inputQueueName, exception);
                }
                finally
                {
                    _inputQueue = null;
                }
            }

            GetInputQueue();

            _log.Info("Input queue handle successfully reinitialized");
        }

        MessageQueue GetInputQueue()
        {
            if (_inputQueue != null) return _inputQueue;

            lock (this)
            {
                if (_inputQueue != null) return _inputQueue;

                var inputQueuePath = MsmqUtil.GetPath(_inputQueueName);

                MsmqUtil.EnsureQueueExists(inputQueuePath, _log, false);
  
                _inputQueue = new MessageQueue(inputQueuePath, QueueAccessMode.SendAndReceive)
                {              
                    MessageReadPropertyFilter = new MessagePropertyFilter
                    {
                        Id = true,
                        Extension = true,
                        Body = true,
                    }
                };
            }

            return _inputQueue;
        }

        class ExtensionSerializer
        {
            public ExtensionSerializer()
            {
                Encoding = Encoding.UTF8;
            }

            public Encoding Encoding { get; set; }

            public byte[] Serialize(Dictionary<string, string> headers)
            {
                var jsonString = JsonConvert.SerializeObject(headers);

                return Encoding.GetBytes(jsonString);
            }

            public Dictionary<string, string> Deserialize(byte[] bytes, string msmqMessageId)
            {
                var jsonString = IsUtf7(bytes)
                    ? Encoding.UTF7.GetString(bytes)
                    : Encoding.GetString(bytes);

                try
                {
                    return JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonString);
                }
                catch (Exception exception)
                {
                    throw new SerializationException($"Could not deserialize MSMQ extension for message with physical message ID {msmqMessageId} - expected valid JSON text, got '{jsonString}'", exception);
                }
            }

            static bool IsUtf7(byte[] bytes)
            {
                // auto-detect UTF7-encoded headers
                // 43, 65, 72, 115, 45 == an UTF7-encoded '{'
                if (bytes.Length < 5) return false;

                return bytes[0] == 43 && bytes[1] == 65 && bytes[2] == 72 && bytes[3] == 115 && bytes[4] == 45;
            }
        }

        /// <summary>
        /// Disposes the input message queue object
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the input queue instance
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            try
            {
                if (disposing)
                {
                    _inputQueue?.Dispose();
                    _inputQueue = null;
                }
            }
            finally
            {
                _disposed = true;
            }
        }

        /// <summary>
        /// Configures the transport to serialize headers in "legacy mode", which means that they're UTF7-encoded and not UTF8
        /// </summary>
        public void UseLegacyHeaderSerialization()
        {
            _extensionSerializer.Encoding = Encoding.UTF7;
        }

        /// <summary>
        /// Indicates whether the queue is transactional
        /// </summary>
        public bool? IsTransactional => _inputQueue?.Transactional;
    }
}