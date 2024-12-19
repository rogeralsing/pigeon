﻿//-----------------------------------------------------------------------
// <copyright file="TestJournal.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

namespace Akka.Persistence.TestKit
{
    using Akka.Actor;
    using Akka.Persistence;
    using Akka.Persistence.Journal;
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Threading.Tasks;

    /// <summary>
    ///     In-memory persistence journal implementation which behavior could be controlled by interceptors.
    /// </summary>
    public sealed class TestJournal : MemoryJournal
    {
        private IJournalInterceptor _writeInterceptor = JournalInterceptors.Noop.Instance;
        private IJournalInterceptor _recoveryInterceptor = JournalInterceptors.Noop.Instance;
        private IConnectionInterceptor _connectionInterceptor = ConnectionInterceptors.Noop.Instance;

        protected override bool ReceivePluginInternal(object message)
        {
            switch (message)
            {
                case UseWriteInterceptor use:
                    _writeInterceptor = use.Interceptor;
                    Sender.Tell(Ack.Instance);
                    return true;

                case UseRecoveryInterceptor use:
                    _recoveryInterceptor = use.Interceptor;
                    Sender.Tell(Ack.Instance);
                    return true;
                
                case UseConnectionInterceptor use:
                    _connectionInterceptor = use.Interceptor;
                    Sender.Tell(Ack.Instance);
                    return true;
                
                default:
                    return base.ReceivePluginInternal(message);
            }
        }

        protected override async Task<IImmutableList<Exception>> WriteMessagesAsync(IEnumerable<AtomicWrite> messages)
        {
            await _connectionInterceptor.InterceptAsync();
            var exceptions = new List<Exception>();
            foreach (var w in messages)
            {
                try
                {
                    foreach (var p in (IEnumerable<IPersistentRepresentation>)w.Payload)
                    {
                        await _writeInterceptor.InterceptAsync(p);
                        Add(p);
                    }
                }
                catch (TestJournalRejectionException rejected)
                {
                    // i.e. problems with data: corrupted data-set, problems in serialization, constraints, etc.
                    exceptions.Add(rejected);
                    continue;
                }
                catch (TestJournalFailureException)
                {
                    // i.e. data-store problems: network, invalid credentials, etc.
                    throw;
                }
                exceptions.Add(null);
            }

            return exceptions.ToImmutableList();
        }

        public override async Task ReplayMessagesAsync(IActorContext context, string persistenceId, long fromSequenceNr, long toSequenceNr, long max, Action<IPersistentRepresentation> recoveryCallback)
        {
            await _connectionInterceptor.InterceptAsync();
            var highest = HighestSequenceNr(persistenceId);
            if (highest != 0L && max != 0L)
            {
                var messages = Read(persistenceId, fromSequenceNr, Math.Min(toSequenceNr, highest), max);
                foreach (var p in messages)
                {
                    try
                    {
                        await _recoveryInterceptor.InterceptAsync(p);
                        recoveryCallback(p);
                    }
                    catch (TestJournalFailureException)
                    {
                        // i.e. problems with data: corrupted data-set, problems in serialization
                        // i.e. data-store problems: network, invalid credentials, etc.
                        throw;
                    }
                }
            }
        }

        public override async Task<long> ReadHighestSequenceNrAsync(string persistenceId, long fromSequenceNr)
        {
            await _connectionInterceptor.InterceptAsync();
            return await base.ReadHighestSequenceNrAsync(persistenceId, fromSequenceNr);
        }

        /// <summary>
        ///     Create proxy object from journal actor reference which can alter behavior of journal.
        /// </summary>
        /// <remarks>
        ///     Journal actor must be of <see cref="TestJournal"/> type.
        /// </remarks>
        /// <param name="actor">Journal actor reference.</param>
        /// <returns>Proxy object to control <see cref="TestJournal"/>.</returns>
        public static ITestJournal FromRef(IActorRef actor)
        {
            return new TestJournalWrapper(actor);
        }

        public sealed class UseWriteInterceptor
        {
            public UseWriteInterceptor(IJournalInterceptor interceptor)
            {
                Interceptor = interceptor;
            }

            public IJournalInterceptor Interceptor { get; }
        }

        public sealed class UseRecoveryInterceptor
        {
            public UseRecoveryInterceptor(IJournalInterceptor interceptor)
            {
                Interceptor = interceptor;
            }

            public IJournalInterceptor Interceptor { get; }
        }

        public sealed class UseConnectionInterceptor
        {
            public UseConnectionInterceptor(IConnectionInterceptor interceptor)
            {
                Interceptor = interceptor;
            }

            public IConnectionInterceptor Interceptor { get; }
        }
        
        public sealed class Ack
        {
            public static readonly Ack Instance = new();
        }

        internal class TestJournalWrapper : ITestJournal
        {
            public TestJournalWrapper(IActorRef actor)
            {
                _actor = actor;
            }

            private readonly IActorRef _actor;

            public JournalWriteBehavior OnWrite => new(new JournalWriteBehaviorSetter(_actor));

            public JournalRecoveryBehavior OnRecovery => new(new JournalRecoveryBehaviorSetter(_actor));
            
            public JournalConnectionBehavior OnConnect => new(new JournalConnectionBehaviorSetter(_actor));
        }
    }
}
