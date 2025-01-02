// -----------------------------------------------------------------------
//  <copyright file="IShardingMessageAdapter.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Akka.Annotations;

namespace Akka.Cluster.Sharding;

[InternalApi]
public interface IShardingMessageAdapter
{
    public object Adapt(object message);
}

[InternalApi]
internal class EmptyMessageAdapter: IShardingMessageAdapter
{
    public static EmptyMessageAdapter Instance { get; } = new ();

    private EmptyMessageAdapter()
    {
    }
        
    public object Adapt(object message) => message;
}
