﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Messages;
using Rebus.Pipeline;
using Rebus.Profiling;
using Rebus.Tests.Contracts;
using Rebus.Transport;

namespace Rebus.Tests.Profiling
{
    [TestFixture]
    public class TestProfiler : FixtureBase
    {
        [Test]
        public void CanMeasureTimeSpentInSteps()
        {
            var stats = new PipelineStepProfilerStats();
            var pipeline = new DefaultPipeline()
                .OnReceive(new Step300())
                .OnReceive(new Step100())
                .OnReceive(new Step200());

            var profiler = new PipelineStepProfiler(pipeline, stats);

            var receivePipeline = profiler.ReceivePipeline();
            var invoker = new DefaultPipelineInvoker();
            var transportMessage = new TransportMessage(new Dictionary<string, string>(), new byte[0]);

            using (new DefaultTransactionContextScope())
            {
                var stepContext = new IncomingStepContext(transportMessage, AmbientTransactionContext.Current);

                invoker.Invoke(stepContext, receivePipeline).Wait();

                var stepStats = stats.GetStats();

                Console.WriteLine(string.Join(Environment.NewLine, stepStats));
            }
        }

        class Step100 : IIncomingStep
        {
            public async Task Process(IncomingStepContext context, Func<Task> next)
            {
                await Task.Delay(100);
                await next();
            }
        }

        class Step200 : IIncomingStep
        {
            public async Task Process(IncomingStepContext context, Func<Task> next)
            {
                await Task.Delay(200);
                await next();
            }
        }

        class Step300 : IIncomingStep
        {
            public async Task Process(IncomingStepContext context, Func<Task> next)
            {
                await Task.Delay(300);
                await next();
            }
        }
    }
}