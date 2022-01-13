//-----------------------------------------------------------------------
// <copyright file="JournalBenchmarks.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Benchmarks.Configurations;
using BenchmarkDotNet.Attributes;
using static Akka.Cluster.Benchmarks.Persistence.PersistenceInfrastructure;

namespace Akka.Cluster.Benchmarks.Persistence
{
    [Config(typeof(MonitoringConfig))]
    public class JournalWriteBenchmarks
    {
        private static readonly Store Message = new Store(1);

        [Params(1, 10, 100)] public int PersistentActors;

        [Params(100)] public int WriteMsgCount;

        private ActorSystem _sys1;

        private IActorRef _doneActor;
        private HashSet<IActorRef> _persistentActors;
        private HashSet<Store> _msgs;

        /*
        * Don't need to worry about cleaning up in-memory SQLite databases: https://www.sqlite.org/inmemorydb.html
        * Database is automatically deleted once the last connection to it is closed.
        */

        [IterationSetup]
        public void Setup()
        {
            var (connectionStr, config) = GenerateJournalConfig();
            _sys1 = ActorSystem.Create("MySys", config);
            _doneActor = _sys1.ActorOf(Props.Create(() => new BenchmarkDoneActor(PersistentActors)), "done");
            _persistentActors = new HashSet<IActorRef>();
            _msgs = new HashSet<Store>();

            var tasks = new List<Task<Done>>();
            var startupCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            foreach (var i in Enumerable.Range(0, PersistentActors))
            {
                var myRef = _sys1.ActorOf(Props.Create(() => new PerformanceTestActor(i.ToString(), _doneActor, WriteMsgCount)), i.ToString());
                _persistentActors.Add(myRef);
                tasks.Add(myRef.Ask<Done>(Init.Instance, startupCts.Token));
            }

            // precalculate all messages sent to actors
            foreach (var i in Enumerable.Range(0, WriteMsgCount))
            {
                _msgs.Add(new Store(i));
            }

            // all persistence actors have started and successfully communicated with journal
            Task.WhenAll(tasks).Wait(startupCts.Token);
        }

        [IterationCleanup]
        public void Cleanup()
        {
            _sys1.Terminate().Wait();
        }

        [Benchmark]
        public async Task WriteToPersistence()
        {
            var startupCts = new CancellationTokenSource(TimeSpan.FromMinutes(1));

            var completionTask = _doneActor.Ask<Finished>(IsFinished.Instance, startupCts.Token);

            foreach(var _ in Enumerable.Range(0, WriteMsgCount))
            foreach (var a in _persistentActors)
            {
                a.Tell(Message);
            }

            await completionTask;
        }
    }
}