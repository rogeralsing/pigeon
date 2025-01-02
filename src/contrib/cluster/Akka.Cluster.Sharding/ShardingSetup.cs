// -----------------------------------------------------------------------
//  <copyright file="ShardingSetup.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Akka.Actor.Setup;
using Akka.Annotations;

#nullable enable
namespace Akka.Cluster.Sharding;

[InternalApi]
public class ShardingSetup: Setup
{
    public static ShardingSetup Create(IShardingMessageAdapter messageAdapter)
        => new (messageAdapter);
    
    internal ShardingSetup(IShardingMessageAdapter messageAdapter)
    {
        MessageAdapter = messageAdapter;
    }

    public IShardingMessageAdapter MessageAdapter { get; }
}
