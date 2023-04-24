﻿//-----------------------------------------------------------------------
// <copyright file="StashCapacitySpecs.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2023 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2023 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;

namespace Akka.Tests.Actor.Stash;

public class StashCapacitySpecs : AkkaSpec
{
    public StashCapacitySpecs(ITestOutputHelper output) : base(Config.Empty, output: output)
    {
        
    }

    [Fact]
    public async Task ShouldGetAccurateStashReadingForUnboundedStash()
    {
        var stashActor = Sys.ActorOf(Props.Create(() => new StashActor()));
        stashActor.Tell(new StashActor.StashMessage("1"));
        stashActor.Tell(new StashActor.StashMessage("2"));
        stashActor.Tell(StashActor.GetStashReadout.Instance);
        var readout1 = await ExpectMsgAsync<StashActor.StashReadout>();
        readout1.Capacity.Should().Be(-1); // unbounded stash returns -1 for capacity
        readout1.Size.Should().Be(2);
        readout1.IsEmpty.Should().BeFalse();
        readout1.IsFull.Should().BeFalse();
        
        stashActor.Tell(StashActor.UnstashMessage.Instance);
        stashActor.Tell(StashActor.GetStashReadout.Instance);
        var readout2 = await ExpectMsgAsync<StashActor.StashReadout>();
        readout2.Capacity.Should().Be(-1);
        readout2.Size.Should().Be(1);
        readout2.IsEmpty.Should().BeFalse();
        readout2.IsFull.Should().BeFalse();
        
        stashActor.Tell(StashActor.UnstashMessage.Instance);
        stashActor.Tell(StashActor.GetStashReadout.Instance);
        var readout3 = await ExpectMsgAsync<StashActor.StashReadout>();
        readout3.Capacity.Should().Be(-1);
        readout3.Size.Should().Be(0);
        readout3.IsEmpty.Should().BeTrue();
        readout3.IsFull.Should().BeFalse();
    }

    private class StashActor : UntypedActorWithStash
    {
        public class StashMessage
        {
            public StashMessage(string message)
            {
                Message = message;
            }

            public string Message { get; }
        }
        
        public class UnstashMessage
        {
            private UnstashMessage()
            {
                
            }
            public static readonly UnstashMessage Instance = new();
        }
        
        public class GetStashReadout
        {
            private GetStashReadout()
            {
                
            }
            public static readonly GetStashReadout Instance = new();
        }
        
        public class StashReadout
        {
            public StashReadout(int capacity, int size, bool isEmpty, bool isFull)
            {
                Capacity = capacity;
                Size = size;
                IsEmpty = isEmpty;
                IsFull = isFull;
            }

            public int Capacity { get; }
            public int Size { get; }
            
            public bool IsEmpty { get; }
            
            public bool IsFull { get; }
        }
        
        protected override void OnReceive(object message)
        {
            switch (message)
            {
                case StashMessage msg:
                    Stash.Stash();
                    break;
                case UnstashMessage _:
                    Stash.Unstash();
                    Context.Become(Unstashing); // switch behaviors so we're not stuck in stash loop
                    break;
                case GetStashReadout _:
                    Sender.Tell(new StashReadout(Stash.Capacity, Stash.Count, Stash.IsEmpty, Stash.IsFull));
                    break;
                default:
                    Unhandled(message);
                    break;
            }
        }

        private void Unstashing(object message)
        {
            switch (message)
            {
                case StashMessage msg: // do nothing
                    break;
                case UnstashMessage when Stash.NonEmpty:
                    Stash.Unstash();
                    break;
                case UnstashMessage _: // when the stash is empty, go back to stashing
                    Context.Become(OnReceive);
                    break;
                case GetStashReadout _:
                    Sender.Tell(new StashReadout(Stash.Capacity, Stash.Count, Stash.IsEmpty, Stash.IsFull));
                    break;
                default:
                    Unhandled(message);
                    break;
            }
        }
    }
}