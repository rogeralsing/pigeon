// -----------------------------------------------------------------------
//  <copyright file="DDataShardCoordinatorStateSerializationBenchmarks.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Akka.Actor;
using Akka.Benchmarks.Configurations;
using Akka.Cluster.Sharding;
using Akka.Actor.Dsl;
using Akka.Cluster.Sharding.Serialization.Proto.Msg;
using Akka.Cluster.Tests;
using Akka.Configuration;
using Akka.DistributedData;
using Akka.DistributedData.Serialization;
using Akka.Util;
using BenchmarkDotNet.Attributes;

namespace Akka.Cluster.Benchmarks.Serialization;

[Config(typeof(MicroBenchmarkConfig))]
public class DDataShardCoordinatorStateSerializationBenchmarks
{
    private static readonly Config BaseConfig = ConfigurationFactory.ParseString("""
                akka.actor.provider=cluster
                akka.remote.dot-netty.tcp.port = 0
        """).WithFallback(ClusterSharding.DefaultConfig());
    private ExtendedActorSystem _system;
    private ReplicatedDataSerializer _replicatedDataSerializer;
    
    private static readonly Member FakeNode = TestMember.Create(new Address("akka.tcp", "sys", "b", 2552), MemberStatus.Up,
        ImmutableHashSet.Create("r1"), appVersion: AppVersion.Create("1.1.0"));
    
    [Params(1, 20, 100, 1000)]
    public int ShardCount { get; set; }
    
    [Params(1, 20, 100)]
    public int RegionCount { get; set; }
    
    // Used to represent shards and regions
    private IActorRef _placeHolder;
    
    private readonly LWWRegisterKey<ShardCoordinator.CoordinatorState> _coordinatorStateKey = new("CoordinatorState");
    
    private LWWRegister<ShardCoordinator.CoordinatorState> _coordinatorState;
    private byte[] _seralizedCoordinatorState;
    private string _manifest = string.Empty;

    private static ShardCoordinator.CoordinatorState ComputedState(IActorRef placeholder, int shardCount,
        int regionCount)
    {
        var shards = Enumerable.Range(0, shardCount)
            .Select(i => new KeyValuePair<string, IActorRef>($"shard-{i}", placeholder))
            .ToImmutableDictionary( );
        
        // evenly allocate shards to regions
        var shardsPerRegionCount = shardCount / regionCount; // region count can't be zero
        var shardsPerRegion = shards
            .Chunk(shardsPerRegionCount)
            .Select(c =>
                new KeyValuePair<IActorRef, IImmutableList<string>>(placeholder,
                    c.Select(d => d.Key).ToImmutableList()))
            .ToImmutableDictionary();

        var regionProxies = ImmutableHashSet<IActorRef>.Empty;
        var unallocatedShards = ImmutableHashSet<string>.Empty;
        
        return new ShardCoordinator.CoordinatorState(shards, shardsPerRegion, regionProxies, unallocatedShards);
    }
    
    [GlobalSetup]
    public void Setup()
    {
        _system ??= (ExtendedActorSystem)ActorSystem.Create("system", BaseConfig);
        _placeHolder ??= _system.ActorOf(ctx => { });
        _coordinatorState = new LWWRegister<ShardCoordinator.CoordinatorState>(FakeNode.UniqueAddress, 
            ComputedState(_placeHolder, ShardCount, RegionCount));
        _replicatedDataSerializer = new ReplicatedDataSerializer(_system);
        _seralizedCoordinatorState = _replicatedDataSerializer.ToBinary(_coordinatorState);
        _manifest = _replicatedDataSerializer.Manifest(_coordinatorState);
    }

    /// <summary>
    /// Serialize the raw LWWRegister payload
    /// </summary>
    [Benchmark]
    public byte[] SerializeCoordinatorState()
    {
        return _replicatedDataSerializer.ToBinary(_coordinatorState);
    }
    
    [Benchmark]
    public object DeserializeCoordinatorState()
    {
        return _replicatedDataSerializer.FromBinary(_seralizedCoordinatorState, _manifest);
    } 
    
    [GlobalCleanup]
    public void Cleanup()
    {
        _system.Dispose();
    }
}