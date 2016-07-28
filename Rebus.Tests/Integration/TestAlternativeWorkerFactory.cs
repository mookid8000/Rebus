﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Rebus.Activation;
using Rebus.Backoff;
using Rebus.Config;
using Rebus.Logging;
using Rebus.Pipeline;
using Rebus.Tests.Extensions;
using Rebus.Threading;
using Rebus.Threading.TaskParallelLibrary;
using Rebus.Transport;
using Rebus.Transport.InMem;
using Rebus.Workers;
#pragma warning disable 1998

namespace Rebus.Tests.Integration
{
    [TestFixture]
    public class TestAlternativeWorkerFactory : FixtureBase
    {
        [Test]
        public async Task NizzleName()
        {
            var gotMessage = new ManualResetEvent(false);

            using (var activator = new BuiltinHandlerActivator())
            {
                activator.Handle<string>(async s =>
                {
                    Console.WriteLine("Got message: {0}", s);
                    gotMessage.Set();
                });

                Configure.With(activator)
                    .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "bimse"))
                    .Options(o =>
                    {
                        o.Register<IWorkerFactory>(c =>
                        {
                            var transport = c.Get<ITransport>();
                            var pipeline = c.Get<IPipeline>();
                            var pipelineInvoker = c.Get<IPipelineInvoker>();
                            var rebusLoggerFactory = c.Get<IRebusLoggerFactory>();
                            return new AsyncTaskWorkerFactory(transport, pipeline, pipelineInvoker, rebusLoggerFactory);
                        });
                    })
                    .Start();

                await activator.Bus.SendLocal("hej med dig min ven");

                gotMessage.WaitOrDie(TimeSpan.FromSeconds(3));
            }
        }

        [Test]
        public async Task CanReceiveBunchOfMessages()
        {
            var events = new ConcurrentQueue<string>();

            using (var activator = new BuiltinHandlerActivator())
            {
                activator.Handle<string>(async s => events.Enqueue(s));

                Configure.With(activator)
                    .Logging(l => l.Console(minLevel: LogLevel.Info))
                    .Transport(t => t.UseInMemoryTransport(new InMemNetwork(), "bimse"))
                    .Options(o =>
                    {
                        o.Register<IWorkerFactory>(c =>
                        {
                            var transport = c.Get<ITransport>();
                            var pipeline = c.Get<IPipeline>();
                            var pipelineInvoker = c.Get<IPipelineInvoker>();
                            var rebusLoggerFactory = c.Get<IRebusLoggerFactory>();
                            return new AsyncTaskWorkerFactory(transport, pipeline, pipelineInvoker, rebusLoggerFactory);
                        });
                        o.SetNumberOfWorkers(100);
                    })
                    .Start();

                var bus = activator.Bus;

                await Task.WhenAll(Enumerable.Range(0, 100)
                    .Select(i => bus.SendLocal($"msg-{i}")));

                await Task.Delay(1000);

                Assert.That(events.Count, Is.EqualTo(100));
            }
        }

        class AsyncTaskWorkerFactory : IWorkerFactory
        {
            readonly ITransport _transport;
            readonly IPipeline _pipeline;
            readonly IPipelineInvoker _pipelineInvoker;
            readonly IRebusLoggerFactory _rebusLoggerFactory;

            public AsyncTaskWorkerFactory(ITransport transport, IPipeline pipeline, IPipelineInvoker pipelineInvoker, IRebusLoggerFactory rebusLoggerFactory)
            {
                _transport = transport;
                _pipeline = pipeline;
                _pipelineInvoker = pipelineInvoker;
                _rebusLoggerFactory = rebusLoggerFactory;
            }

            public IWorker CreateWorker(string workerName)
            {
                return new AsyncTaskWorker(workerName, _transport, _pipeline, _pipelineInvoker, 10, _rebusLoggerFactory);
            }
        }

        class AsyncTaskWorker : IWorker
        {
            readonly SimpleConstantPollingBackoffStrategy _backoffHelper = new SimpleConstantPollingBackoffStrategy();
            readonly ParallelOperationsManager _parallelOperationsManager;
            readonly ITransport _transport;
            readonly IPipeline _pipeline;
            readonly IPipelineInvoker _pipelineInvoker;
            readonly TplAsyncTask _workerTask;
            readonly ILog _log;

            CancellationTokenSource _workerStopped = new CancellationTokenSource();

            public AsyncTaskWorker(string name, ITransport transport, IPipeline pipeline, IPipelineInvoker pipelineInvoker, int maxParallelismPerWorker, IRebusLoggerFactory rebusLoggerFactory)
            {
                _transport = transport;
                _pipeline = pipeline;
                _pipelineInvoker = pipelineInvoker;
                _parallelOperationsManager = new ParallelOperationsManager(maxParallelismPerWorker);
                _log = rebusLoggerFactory.GetCurrentClassLogger();

                Name = name;

                _workerTask = new TplAsyncTask(name, DoWork, new ConsoleLoggerFactory(false), prettyInsignificant: true)
                {
                    Interval = TimeSpan.FromMilliseconds(1)
                };
                _log.Debug("Starting (task-based) worker {0}", Name);
                _workerTask.Start();
            }

            async Task DoWork()
            {
                using (var op = _parallelOperationsManager.PeekOperation(_workerStopped.Token))
                {
                    using (var transactionContext = new DefaultTransactionContext())
                    {
                        AmbientTransactionContext.Current = transactionContext;
                        try
                        {
                            var message = await _transport.Receive(transactionContext);

                            if (message == null)
                            {
                                // finish the tx and wait....
                                await transactionContext.Complete();
                                await _backoffHelper.Wait();
                                return;
                            }

                            _backoffHelper.Reset();

                            var context = new IncomingStepContext(message, transactionContext);
                            transactionContext.Items[StepContext.StepContextKey] = context;

                            var stagedReceiveSteps = _pipeline.ReceivePipeline();

                            await _pipelineInvoker.Invoke(context, stagedReceiveSteps);

                            await transactionContext.Complete();
                        }
                        catch (Exception exception)
                        {
                            _log.Error(exception, "Unhandled exception in task worker");
                        }
                        finally
                        {
                            AmbientTransactionContext.Current = null;
                        }
                    }
                }
            }

            public string Name { get; }

            public void Stop()
            {
                DisposeTask();
            }

            public void Dispose()
            {
                DisposeTask();
            }

            void DisposeTask()
            {
                if (_workerStopped.IsCancellationRequested)
                    return;

                _workerStopped.Cancel();
                _workerTask.Dispose();
                _log.Debug("Worker {0} stopped", Name);
            }
        }
    }
}