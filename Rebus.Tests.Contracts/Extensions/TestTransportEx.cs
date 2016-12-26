﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Messages;
using Rebus.Transport;

namespace Rebus.Tests.Contracts.Extensions
{
    public static class TestTransportEx
    {
        public static async Task<TransportMessage> AwaitReceive(this ITransport transport, double timeoutSeconds = 5)
        {
            var stopwatch = Stopwatch.StartNew();
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            var source = new CancellationTokenSource();

            while (stopwatch.Elapsed < timeout)
            {
                TransportMessage receivedTransportMessage;

                using (var transactionContext = new DefaultTransactionContextScope())
                {
                    receivedTransportMessage = await transport.Receive(AmbientTransactionContext.Current, source.Token);

                    await transactionContext.Complete();
                }

                if (receivedTransportMessage != null) return receivedTransportMessage;
            }

            throw new AssertionException($"Did not receive transport message from {transport} within {timeout} timeout");
        }
    }
}