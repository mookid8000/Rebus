﻿using Rebus.Bus;
using Rebus.Transport;
using Rebus.Transport.Msmq;

namespace Rebus.Config
{
    /// <summary>
    /// Helper that gives a backdoor to the configuration <see cref="Options"/>, allowing for one-way client settings
    /// to be set.
    /// </summary>
    public class OneWayClientBackdoor
    {
        /// <summary>
        /// Uses the given <see cref="StandardConfigurer{TService}"/> of <see cref="ITransport"/> to set the number of workers
        /// to zero (effectively disabling message processing) and installs a decorator of <see cref="IBus"/> that prevents
        /// further modification of the number of workers (thus preventing accidentally starting workers when there's no input queue).
        /// </summary>
        public static void ConfigureOneWayClient(StandardConfigurer<ITransport> configurer)
        {
            configurer.Options.NumberOfWorkers = 0;

            configurer.OtherService<IBus>().Decorate(c => new OneWayClientBusDecorator(c.Get<IBus>()));
        } 
    }
}