﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Transactions;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;
using Rebus.Logging;
using Rebus.Messages;
using Rebus.Serialization;
using Rebus.Transports.Msmq;

namespace Rebus.Transports.Azure.AzureMessageQueue
{
    public class AzureMessageQueue : ISendMessages, IReceiveMessages, IHavePurgableInputQueue<AzureMessageQueue>
    {
        static readonly ILog Log = RebusLoggerFactory.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private readonly CloudStorageAccount cloudStorageAccount;
        private readonly string inputQueueName;
        private readonly CloudQueueClient cloudQueueClient;
        readonly ConcurrentDictionary<string, CloudQueue> outputQueues = new ConcurrentDictionary<string, CloudQueue>();
        private readonly CloudQueue inputQueue;
        private readonly DictionarySerializer _dictionarySerializer;

        public AzureMessageQueue(CloudStorageAccount cloudStorageAccount, string inputQueueName)
        {
            this.cloudStorageAccount = cloudStorageAccount;
            this.inputQueueName = inputQueueName;
            cloudQueueClient = this.cloudStorageAccount.CreateCloudQueueClient();
            inputQueue = cloudQueueClient.GetQueueReference(inputQueueName);

            
            _dictionarySerializer = new DictionarySerializer();
        }

        public void Send(string destinationQueueName, TransportMessageToSend message)
        {
            CloudQueue outputQueue;

            if (!outputQueues.TryGetValue(destinationQueueName, out outputQueue))
            {
                lock (outputQueues)
                {
                    if (!outputQueues.TryGetValue(destinationQueueName, out outputQueue))
                    {
                        outputQueue = cloudQueueClient.GetQueueReference(destinationQueueName);
                        outputQueue.CreateIfNotExist();
                        outputQueues[destinationQueueName] = outputQueue;
                    }
                }
            }


            message.Headers = message.Headers ?? new Dictionary<string, string>();

            var headers = _dictionarySerializer.Serialize(message.Headers);

            var cloudMessage = new CloudQueueMessage(Encoding.UTF7.GetBytes(headers + Environment.NewLine + message.Data));

            var timeToLive = GetTimeToLive(message);

            if (timeToLive.HasValue)
                outputQueue.AddMessage(cloudMessage, timeToLive.Value);
            else
                outputQueue.AddMessage(cloudMessage);
        }

        private TimeSpan? GetTimeToLive(TransportMessageToSend message)
        {
            if (message.Headers != null && message.Headers.ContainsKey(Headers.TimeToBeReceived))
            {
                return TimeSpan.Parse(message.Headers[Headers.TimeToBeReceived]);
            }

            return null;
        }

        public ReceivedTransportMessage ReceiveMessage()
        {
            var azureMessageQueueTransactionSimulator = new AzureMessageQueueTransactionSimulator(inputQueue);
            try
            {
                var message = azureMessageQueueTransactionSimulator.RetrieveCloudQueueMessage = inputQueue.GetMessage();

                if (message == null)
                {
                    //No message receieved
                    azureMessageQueueTransactionSimulator.Commit();
                    return null;
                }

                var rawData = message.AsBytes;

                if (rawData == null)
                {
                    Log.Warn("Received message with NULL data - how weird is that?");
                    azureMessageQueueTransactionSimulator.Commit();
                    return null;
                }

                var allData = Encoding.UTF7.GetString(rawData);
                var dataSplitIndex = allData.IndexOf(Environment.NewLine, StringComparison.Ordinal);

                var headerData = allData.Substring(0, dataSplitIndex);
                var headers = _dictionarySerializer.Deserialize(headerData);

                var messageData = allData.Substring(dataSplitIndex + Environment.NewLine.Length);

                var receivedTransportMessage = new ReceivedTransportMessage()
                                                   {
                                                       Data = messageData,
                                                       Id = message.Id,
                                                       Headers = headers
                                                   };

                azureMessageQueueTransactionSimulator.Commit();

                return receivedTransportMessage;
            }
            catch (Exception e)
            {
                Log.Error(e, "An error occurred while receiving message from {0}", inputQueueName);
                azureMessageQueueTransactionSimulator.Abort();
                return null;
            }
        }

        public string InputQueue
        {
            get { return inputQueueName; }
        }

        #region Implementation of IHavePurgableInputQueue<AzureMessageQueue>

        public AzureMessageQueue PurgeInputQueue()
        {
            Log.Warn("Purging {0}", inputQueueName);

            if (inputQueue.Exists())
                inputQueue.Clear();

            return this;
        }

        #endregion
    }
}
