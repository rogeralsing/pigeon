﻿//-----------------------------------------------------------------------
// <copyright file="DeadLettersSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2024 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2024 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System.Threading.Tasks;
using Akka.Actor;
using Akka.Event;
using Akka.TestKit;
using Xunit;

namespace Akka.Tests
{

    public class WrappedMessagesSpec
    {
        private sealed record WrappedClass(object Message) : IWrappedMessage;
        
        private sealed record WrappedSuppressedClass(object Message) : IWrappedMessage, IDeadLetterSuppression;
        
        private sealed class SuppressedMessage : IDeadLetterSuppression
        {
            
        }
        
        
        [Fact]
        public void ShouldUnwrapWrappedMessage()
        {
            var message = new WrappedClass("chocolate-beans");
            var unwrapped = WrappedMessage.Unwrap(message);
            unwrapped.ShouldBe("chocolate-beans");
        }
        
        public static readonly TheoryData<object, bool> SuppressedMessages = new()
        {
            {new SuppressedMessage(), true},
            {new WrappedClass(new SuppressedMessage()), true},
            {new WrappedClass(new WrappedClass(new SuppressedMessage())), true},
            {new WrappedClass(new WrappedClass("chocolate-beans")), false},
            {new WrappedSuppressedClass("foo"), true},
            {new WrappedClass(new WrappedSuppressedClass("chocolate-beans")), true},
            {new WrappedClass("chocolate-beans"), false},
            {"chocolate-beans", false}
        };
        
        [Theory]
        [MemberData(nameof(SuppressedMessages))]
        public void ShouldDetectIfWrappedMessageIsSuppressed(object message, bool expected)
        {
            var isSuppressed = WrappedMessage.IsDeadLetterSuppressedAnywhere(message, out _);
            isSuppressed.ShouldBe(expected);
        }
    }
    
    public class DeadLettersSpec : AkkaSpec
    {
        [Fact]
        public async Task Can_send_messages_to_dead_letters()
        {
            Sys.EventStream.Subscribe(TestActor, typeof(DeadLetter));
            Sys.DeadLetters.Tell("foobar");
            await ExpectMsgAsync<DeadLetter>(deadLetter=>deadLetter.Message.Equals("foobar"));
        }

        private sealed record WrappedClass(object Message) : IWrappedMessage;
        
        private sealed class SuppressedMessage : IDeadLetterSuppression
        {
            
        }

        [Fact]
        public async Task ShouldLogNormalWrappedMessages()
        {
            Sys.EventStream.Subscribe(TestActor, typeof(DeadLetter));
            Sys.DeadLetters.Tell(new WrappedClass("chocolate-beans"));
                
            // this is just to make the test deterministic
            await ExpectMsgAsync<DeadLetter>();
        }
        
        [Fact]
        public async Task ShouldNotLogWrappedMessagesWithDeadLetterSuppression()
        {
            Sys.EventStream.Subscribe(TestActor, typeof(AllDeadLetters));
            Sys.DeadLetters.Tell(new WrappedClass(new SuppressedMessage()));
                
            // this is just to make the test deterministic
            var msg = await ExpectMsgAsync<SuppressedDeadLetter>();
            msg.Message.ToString()!.Contains("SuppressedMessage").ShouldBeTrue();
        }
    }
}

