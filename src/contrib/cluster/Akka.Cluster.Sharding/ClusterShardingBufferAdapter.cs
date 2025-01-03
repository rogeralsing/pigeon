// -----------------------------------------------------------------------
//  <copyright file="ShardingBufferAdapter.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Akka.Actor;
using Akka.Annotations;

#nullable enable
namespace Akka.Cluster.Sharding;

[InternalApi]
public class ClusterShardingBufferAdapter : IExtension
{
    public static ClusterShardingBufferAdapter Get(ActorSystem system)
    {
        return system.WithExtension<ClusterShardingBufferAdapter, ClusterShardingBufferAdapterExtensionProvider>();
    }

    public IShardingBufferMessageAdapter BufferMessageAdapter { get; private set; } = EmptyBufferMessageAdapter.Instance;

    public void SetShardingBufferMessageAdapter(IShardingBufferMessageAdapter? bufferMessageAdapter)
    {
        BufferMessageAdapter = bufferMessageAdapter ?? EmptyBufferMessageAdapter.Instance;
    }
}

[InternalApi]
public sealed class ClusterShardingBufferAdapterExtensionProvider : ExtensionIdProvider<ClusterShardingBufferAdapter>
{
    public override ClusterShardingBufferAdapter CreateExtension(ExtendedActorSystem system) => new ();
}
