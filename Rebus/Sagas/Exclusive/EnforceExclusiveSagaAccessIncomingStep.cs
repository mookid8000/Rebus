﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Pipeline.Receive;
using Rebus.Transport;
// ReSharper disable ForCanBeConvertedToForeach

namespace Rebus.Sagas.Exclusive
{
    [StepDocumentation("Enforces exclusive access to saga data in the rest of the pipeline by acquiring locks for the relevant correlation properties.")]
    class EnforceExclusiveSagaAccessIncomingStep : IIncomingStep
    {
        readonly IExclusiveSagaAccessLock _lockHandler;
        readonly CancellationToken _cancellationToken;
        readonly SagaHelper _sagaHelper = new SagaHelper();
        readonly int _maxLockBuckets;

        public EnforceExclusiveSagaAccessIncomingStep(IExclusiveSagaAccessLock lockHandler, int maxLockBuckets, CancellationToken cancellationToken)
        {
            _lockHandler = lockHandler;
            _maxLockBuckets = maxLockBuckets;
            _cancellationToken = cancellationToken;
        }

        public async Task Process(IncomingStepContext context, Func<Task> next)
        {
            var handlerInvokersForSagas = context.Load<HandlerInvokers>()
                .Where(l => l.HasSaga)
                .ToList();

            if (!handlerInvokersForSagas.Any())
            {
                await next();
                return;
            }

            var message = context.Load<Message>();
            var transactionContext = context.Load<ITransactionContext>();
            var messageContext = new MessageContext(transactionContext);

            var messageBody = message.Body;

            var correlationProperties = handlerInvokersForSagas
                .Select(h => h.Saga)
                .SelectMany(saga => _sagaHelper.GetCorrelationProperties(messageBody, saga).ForMessage(messageBody)
                    .Select(correlationProperty => new { saga, correlationProperty }))
                    .ToList();

            var locksToObtain = correlationProperties
                .Select(a => new
                {
                    SagaDataType = a.saga.GetSagaDataType().FullName,
                    CorrelationPropertyName = a.correlationProperty.PropertyName,
                    CorrelationPropertyValue = a.correlationProperty.ValueFromMessage(messageContext, messageBody)
                })
                .Select(a => a.ToString())
                .Select(lockId => Math.Abs(lockId.GetHashCode()) % _maxLockBuckets)
                .Distinct() // avoid accidentally acquiring the same lock twice, because a bucket got hit more than once
                .OrderBy(str => str) // enforce consistent ordering to avoid deadlocks
                .ToArray();

            try
            {
                await WaitForLocks(locksToObtain);
                await next();
            }
            finally
            {
                await ReleaseLocks(locksToObtain);
            }
        }

        async Task WaitForLocks(int[] lockIds)
        {
            for (var index = 0; index < lockIds.Length; index++)
            {
                while (!await _lockHandler.AquireLockAsync(lockIds[index], _cancellationToken))
                {
                    await Task.Yield();
                }
            }
        }

        async Task ReleaseLocks(int[] lockIds)
        {
            for (var index = 0; index < lockIds.Length; index++)
            {
                await _lockHandler.ReleaseLockAsync(lockIds[index]);
            }
        }

        public override string ToString() => "EnforceExclusiveSagaAccessIncomingStep";
    }
}