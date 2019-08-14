﻿//-----------------------------------------------------------------------
// <copyright file="SnapshotStoreSaveBehavior.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2019 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2019 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

namespace Akka.Persistence.TestKit
{
    public class SnapshotStoreSaveBehavior
    {
        public SnapshotStoreSaveBehavior(ISnapshotStoreBehaviorSetter setter)
        {
            Setter = setter;
        }

        protected readonly ISnapshotStoreBehaviorSetter Setter;
    }
}