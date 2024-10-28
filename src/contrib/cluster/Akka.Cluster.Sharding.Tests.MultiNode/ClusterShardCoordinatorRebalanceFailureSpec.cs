// -----------------------------------------------------------------------
//  <copyright file="ClusterShardCoordinatorRebalanceFailureSpec.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Cluster.Sharding.Internal;
using Akka.Configuration;
using Akka.MultiNode.TestAdapter;
using Akka.Remote.TestKit;
using FluentAssertions;

namespace Akka.Cluster.Sharding.Tests.MultiNode;

/// <summary>
/// Override the default shard allocation strategy to allow for triggering an exception
/// </summary>
internal class CustomShardAllocationStrategy : Internal.LeastShardAllocationStrategy
{
    private bool _shouldThrow;


    public CustomShardAllocationStrategy(int absoluteLimit, double relativeLimit, bool shouldThrow) : base(absoluteLimit, relativeLimit)
    {
        _shouldThrow = shouldThrow;
    }

    public void TriggerException() => _shouldThrow = true;
    public void ResetException() => _shouldThrow = false;


    public override Task<IImmutableSet<string>> Rebalance(IImmutableDictionary<IActorRef, IImmutableList<string>> currentShardAllocations, IImmutableSet<string> rebalanceInProgress)
    {
        if (_shouldThrow)
            throw new Exception("Simulated exception");

        return base.Rebalance(currentShardAllocations, rebalanceInProgress);
    }
}

public class CustomShardAllocationStrategySpecConfig : MultiNodeClusterShardingConfig
{
    public RoleName First { get; }
    public RoleName Second { get; }
    public RoleName Third { get; }

    public CustomShardAllocationStrategySpecConfig()
    {
        First = Role("first");
        Second = Role("second");
        Third = Role("third");

        CommonConfig = DebugConfig(on: false)
            .WithFallback(ConfigurationFactory.ParseString("""
                                                           
                                                                           akka.cluster.sharding.rebalance-interval = 120 s
                                                                           akka.loglevel = DEBUG
                                                                       
                                                           """));
    }
}

public class SimpleEntityActor : ActorBase
{
    public class Ping
    {
        public string Id { get; }
        public Ping(string id) => Id = id;
    }

    protected override bool Receive(object message)
    {
        if (message is Ping)
        {
            Sender.Tell(Self);
            return true;
        }
        return false;
    }
}

public class MessageExtractor : HashCodeMessageExtractor
{
    public MessageExtractor() : base(30)
    {
    }

    public override string EntityId(object message)
    {
        if (message is SimpleEntityActor.Ping ping)
            return ping.Id;
        return null;
    }
}

public class CustomShardAllocationStrategySpec : MultiNodeClusterShardingSpec<CustomShardAllocationStrategySpecConfig>
{
    private readonly Lazy<IActorRef> _region;
    private readonly CustomShardAllocationStrategy _allocationStrategy;

    public CustomShardAllocationStrategySpec() : this(new CustomShardAllocationStrategySpecConfig())
    {
    }

    protected CustomShardAllocationStrategySpec(CustomShardAllocationStrategySpecConfig config) : base(config, typeof(CustomShardAllocationStrategySpec))
    {
        // use the default least shard allocation parameters
        _allocationStrategy = new CustomShardAllocationStrategy(0, 0.1, false);
        _region = new Lazy<IActorRef>(() => ClusterSharding.Get(Sys).ShardRegion("Entity"));
    }

    private void StartSharding()
    {
        StartSharding(
            Sys,
            typeName: "Entity",
            entityProps: Props.Create(() => new SimpleEntityActor()),
            messageExtractor: new MessageExtractor(),
            allocationStrategy: _allocationStrategy);
    }

    [MultiNodeFact]
    public void CustomShardAllocationStrategySpecTests()
    {
        JoinCluster();
        InitializeShards();
        TriggerRebalanceException();
        VerifyShardReallocation();
    }

    private void JoinCluster()
    {
        Within(TimeSpan.FromSeconds(20), () =>
        {
            Join(Config.First, Config.First, StartSharding);
            Join(Config.Second, Config.First, StartSharding, assertNodeUp: false);
            Join(Config.Third, Config.First, StartSharding, assertNodeUp: false);

            RunOn(() =>
            {
                AwaitAssert(() =>
                {
                    Cluster.State.Members.Count.Should().Be(3);
                    Cluster.State.Members.Should().OnlyContain(m => m.Status == MemberStatus.Up);
                });
            }, Config.First, Config.Second, Config.Third);

            EnterBarrier("cluster-joined");
        });
    }

    private void InitializeShards()
    {
        RunOn(() =>
        {
            for (var i = 0; i < 300; i++)
            {
                _region.Value.Tell(new SimpleEntityActor.Ping(i.ToString()));
            }
        }, Config.First, Config.Second, Config.Third);

        EnterBarrier("shards-initialized");
        
        VerifyShardAllocation(new[] {Config.First, Config.Second, Config.Third});
        EnterBarrier("shards-verified");
    }

    private void TriggerRebalanceException()
    {
        RunOn(() =>
        {
            _allocationStrategy.TriggerException();
            Cluster.Leave(GetAddress(Config.Second));
        }, Config.First);

        RunOn(() =>
        {
            AwaitAssert(() =>
            {
                Cluster.State.Members.Count.Should().Be(2);
                Cluster.State.Members.Should().OnlyContain(m => m.Status == MemberStatus.Up);
            });
        }, Config.First, Config.Third);

        EnterBarrier("node-left");
    }
    
    private void VerifyShardReallocation()
    {
        RunOn(() =>
        {
            // stop erroring out the allocation strategy
            _allocationStrategy.ResetException();
        }, Config.First);
        
        VerifyShardAllocation(new []{ Config.First, Config.Third });
    }

    private void VerifyShardAllocation(RoleName[] rolesWithShards)
    {
        Within(TimeSpan.FromSeconds(10), () =>
        {
            RunOn(() =>
            {
                AwaitAssert(() =>
                {
                    var shardState = ClusterSharding.Get(Sys).ShardRegion("Entity").Ask<CurrentShardRegionState>(GetShardRegionState.Instance, TimeSpan.FromSeconds(3)).Result;
                    shardState.Shards.Count.Should().BeGreaterThan(0);
                });
            }, rolesWithShards);
        });

    }
}