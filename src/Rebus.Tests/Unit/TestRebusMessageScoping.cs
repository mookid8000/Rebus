﻿using System;
using System.Collections.Generic;
using System.Threading;
using Ninject;
using NUnit.Framework;
using Rebus.Ninject;
using Shouldly;

namespace Rebus.Tests.Unit
{
    public class TestRebusMessageScoping
    {
        readonly IKernel kernel;

        public TestRebusMessageScoping()
        {
            kernel = new StandardKernel();
            kernel.Bind<ScopedService>().ToSelf().InRebusMessageScope();
        }

        [Test]
        public void SameMessageContextResultsInSameService()
        {
            using(MessageContext.Establish(new Dictionary<string, object>()))
            {
                var service1 = kernel.Get<ScopedService>();
                var service2 = kernel.Get<ScopedService>();
                service1.ShouldBe(service2);
            }
        }

        [Test]
        public void DifferenctMessageContextsResultsInDifferentServices()
        {
            ScopedService service1;
            ScopedService service2;

            using(MessageContext.Establish(new Dictionary<string, object>()))
                service1 = kernel.Get<ScopedService>();

            using (MessageContext.Establish(new Dictionary<string, object>()))
                service2 = kernel.Get<ScopedService>();

            service1.ShouldNotBe(service2);
        }

        [Test]
        public void ConcurrentMessageContextsResultsInDifferentServices()
        {
            ScopedService service1 = null;
            ScopedService service2 = null;
            
            new Thread(() =>
            {
                using (MessageContext.Establish(new Dictionary<string, object>()))
                {
                    Thread.Sleep(200);
                    service1 = kernel.Get<ScopedService>();
                    while(service2 == null) {}
                }
            }).Start();

            using (MessageContext.Establish(new Dictionary<string, object>()))
            {
                Thread.Sleep(200);
                service2 = kernel.Get<ScopedService>();
                while (service1 == null) { }
            }
            
            service1.ShouldNotBe(service2);
        }

        [Test]
        public void CanNotResolveServiceOutsideScope()
        {
            Should.Throw<Exception>(() => kernel.Get<ScopedService>());
        }

        [Test]
        public void ScopeShouldDisposeServiceWhenMessageContextDisposes()
        {
            ScopedService service1;
            using (MessageContext.Establish(new Dictionary<string, object>()))
            {
                service1 = kernel.Get<ScopedService>();
                Assert.That(service1.IsDisposed,Is.False);
            }
            Assert.That(service1.IsDisposed);
        }

        public class ScopedService : IDisposable
        {
            public bool IsDisposed { get; private set; }
            public void Dispose()
            {
                IsDisposed = true;
            }
        }
    }
}