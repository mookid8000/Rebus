﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rebus.Extensions;
using Rebus.Messages;
using Rebus.Timeouts;

#pragma warning disable 1998

namespace Rebus.AzureStorageQueues
{
    class DisabledTimeoutManager : ITimeoutManager
    {
        public async Task Defer(DateTimeOffset approximateDueTime, Dictionary<string, string> headers, byte[] body)
        {
            var messageIdToPrint = headers.GetValueOrNull(Headers.MessageId) ?? "<no message ID>";

            var message = string.Format("Received message with ID {0} which is supposed to be deferred until {1} -" +
                                        " this is a problem, because the internal handling of deferred messages is" +
                                        " disabled when using Azure Storage Queues as the transport layer in, which" +
                                        " case the native support for a specific initial visibility delay is used...",
                messageIdToPrint, approximateDueTime);

            throw new InvalidOperationException(message);
        }

        public async Task<DueMessagesResult> GetDueMessages()
        {
            return DueMessagesResult.Empty;
        }
    }
}