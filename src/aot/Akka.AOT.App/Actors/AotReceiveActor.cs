﻿// -----------------------------------------------------------------------
//  <copyright file="AotReceiveActor.cs" company="Akka.NET Project">
//      Copyright (C) 2009-2025 Lightbend Inc. <http://www.lightbend.com>
//      Copyright (C) 2013-2025 .NET Foundation <https://github.com/akkadotnet/akka.net>
//  </copyright>
// -----------------------------------------------------------------------

using Akka.Actor;

namespace Akka.AOT.App.Actors;

public class AotReceiveActor : ReceiveActor
{
    public AotReceiveActor()
    {
        ReceiveAny(o => Sender.Tell(o));
    }
}