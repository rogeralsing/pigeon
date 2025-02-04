﻿//-----------------------------------------------------------------------
// <copyright file="AddressCache.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2022 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Threading;
using Akka.Actor;

namespace Akka.Remote.Serialization
{
    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal sealed class AddressThreadLocalCache : ExtensionIdProvider<AddressThreadLocalCache>, IExtension
    {
        public AddressThreadLocalCache()
        {
            _current = new ThreadLocal<AddressCache>(() => new AddressCache());
        }

        public override AddressThreadLocalCache CreateExtension(ExtendedActorSystem system)
        {
            return new AddressThreadLocalCache();
        }

        private readonly ThreadLocal<AddressCache> _current;

        public AddressCache Cache => _current.Value;

        public static AddressThreadLocalCache For(ActorSystem system)
        {
            return system.WithExtension<AddressThreadLocalCache, AddressThreadLocalCache>();
        }
    }

    /// <summary>
    /// INTERNAL API
    /// </summary>
    internal sealed class AddressCache : LruBoundedCache<string, Address>
    {
        public AddressCache(int capacity = 1024, int evictAgeThreshold = 600) 
            : base(capacity, evictAgeThreshold, FastHashComparer.Default)
        {
        }

        protected override Address Compute(string k)
        {
            if (ActorPath.TryParseAddress(k, out var addr))
            {
                return addr;
            }
            return Address.AllSystems;
        }

        protected override bool IsCacheable(Address v)
        {
            return v != Address.AllSystems;
        }
    }
}
