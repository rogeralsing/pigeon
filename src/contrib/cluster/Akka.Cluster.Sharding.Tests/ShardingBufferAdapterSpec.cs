// -----------------------------------------------------------------------
//  <copyright file="ShardingBufferAdapterSpec.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Immutable;
using Akka.Actor;
using Akka.Cluster.Tools.Singleton;
using Akka.Configuration;
using Akka.TestKit;
using Akka.Util.Internal;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;
using static FluentAssertions.FluentActions;

namespace Akka.Cluster.Sharding.Tests;

public class ShardingBufferAdapterSpec: AkkaSpec
{
    private sealed class MessageExtractor: IMessageExtractor
        {
            public string EntityId(object message)
                => message switch
                {
                    int i => i.ToString(),
                    _ => null
                };

            public object EntityMessage(object message)
                => message;

            public string ShardId(object message)
                => message switch
                {
                    int i => (i % 10).ToString(),
                    _ => null
                };

            public string ShardId(string entityId, object messageHint = null)
                => (int.Parse(entityId) % 10).ToString();
        }

    private class EntityActor : ActorBase
    {
        protected override bool Receive(object message)
        {
            Sender.Tell(message);
            return true;
        }
    }
    
    private class TestMessageAdapter: IShardingBufferMessageAdapter
    {
        private readonly AtomicCounter _counter;

        public TestMessageAdapter(AtomicCounter counter)
        {
            _counter = counter;
        }

        public object Apply(object message, IActorContext context)
        {
            _counter.IncrementAndGet();
            return message;
        }
    }

    private const string ShardTypeName = "Caat";

    private static Config SpecConfig =>
        ConfigurationFactory.ParseString(@"
            akka.loglevel = DEBUG
            akka.actor.provider = cluster
            akka.remote.dot-netty.tcp.port = 0
            akka.remote.log-remote-lifecycle-events = off

            akka.test.single-expect-default = 5 s
            akka.cluster.sharding.state-store-mode = ""ddata""
            akka.cluster.sharding.verbose-debug-logging = on
            akka.cluster.sharding.fail-on-invalid-entity-state-transition = on
            akka.cluster.sharding.distributed-data.durable.keys = []")
            .WithFallback(ClusterSingletonManager.DefaultConfig()
            .WithFallback(ClusterSharding.DefaultConfig()));

    private readonly AtomicCounter _counterA = new (0);
    private readonly AtomicCounter _counterB = new (0);

    private readonly ActorSystem _sysA;
    private readonly ActorSystem _sysB;

    private readonly TestProbe _pA;
    private readonly TestProbe _pB;

    private readonly IActorRef _regionA;
    private readonly IActorRef _regionB;
    
    public ShardingBufferAdapterSpec(ITestOutputHelper helper) : base(SpecConfig, helper)
    {
        _sysA = Sys;
        _sysB = ActorSystem.Create(Sys.Name, Sys.Settings.Config);
        
        InitializeLogger(_sysB, "[sysB]");
        
        _pA = CreateTestProbe(_sysA);
        _pB = CreateTestProbe(_sysB);

        ClusterSharding.Get(_sysA).SetShardingBufferMessageAdapter(new TestMessageAdapter(_counterA));
        ClusterSharding.Get(_sysB).SetShardingBufferMessageAdapter(new TestMessageAdapter(_counterB));
        
        _regionA = StartShard(_sysA);
        _regionB = StartShard(_sysB);
    }

    protected override void AfterAll()
    {
        if(_sysA != null)
            Shutdown(_sysA);
        if(_sysB != null)
            Shutdown(_sysB);
        base.AfterAll();
    }
    
    private IActorRef StartShard(ActorSystem sys)
    {
        return ClusterSharding.Get(sys).Start(
            ShardTypeName,
            Props.Create(() => new EntityActor()),
            ClusterShardingSettings.Create(Sys).WithRememberEntities(true),
            new MessageExtractor());
    }
    
    [Fact(DisplayName = "ClusterSharding buffer message adapter must be called when message was buffered")]
    public void ClusterSharding_must_initialize_cluster_and_allocate_sharded_actors()
    {
        Cluster.Get(_sysA).Join(Cluster.Get(_sysA).SelfAddress); // coordinator on A
        
        AwaitAssert(() =>
        {
            Cluster.Get(_sysA).SelfMember.Status.Should().Be(MemberStatus.Up);
        }, TimeSpan.FromSeconds(1));

        Cluster.Get(_sysB).Join(Cluster.Get(_sysA).SelfAddress);

        Within(TimeSpan.FromSeconds(10), () =>
        {
            AwaitAssert(() =>
            {
                foreach (var s in ImmutableHashSet.Create(_sysA, _sysB))
                {
                    Cluster.Get(s).SendCurrentClusterState(TestActor);
                    ExpectMsg<ClusterEvent.CurrentClusterState>().Members.Count.Should().Be(2);
                }
            });
        });

        _regionA.Tell(1, _pA.Ref);
        _pA.ExpectMsg(1);

        _regionB.Tell(2, _pB.Ref);
        _pB.ExpectMsg(2);

        _regionB.Tell(3, _pB.Ref);
        _pB.ExpectMsg(3);

        var counterAValue = _counterA.Current;
        var counterBValue = _counterB.Current;
        
        // Each newly instantiated entities should have their messages buffered at least once
        // Buffer message adapter should be called everytime a message is buffered
        counterAValue.Should().BeGreaterOrEqualTo(1);
        counterBValue.Should().BeGreaterOrEqualTo(2);
        
        _regionA.Tell(1, _pA.Ref);
        _pA.ExpectMsg(1);

        _regionB.Tell(2, _pB.Ref);
        _pB.ExpectMsg(2);

        _regionB.Tell(3, _pB.Ref);
        _pB.ExpectMsg(3);
        
        // Each entity should not have their messages buffered once they were instantiated
        _counterA.Current.Should().Be(counterAValue);
        _counterB.Current.Should().Be(counterBValue);
    }
}