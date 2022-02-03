﻿//-----------------------------------------------------------------------
// <copyright file="DistributedPubSubMediatorSpec.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using Akka.Actor;
using Akka.Configuration;
using Akka.Actor.Dsl;
using Akka.Cluster.Tools.PublishSubscribe;
using Akka.TestKit;
using Xunit;
using Akka.Event;
using System.Threading;
using System.Threading.Tasks;

namespace Akka.Cluster.Tools.Tests.PublishSubscribe
{
    [Collection(nameof(DistributedPubSubMediatorSpec))]
    public class DistributedPubSubMediatorSpec : AkkaSpec
    {
        public DistributedPubSubMediatorSpec() : base(GetConfig()) { }

        public static Config GetConfig()
        {
            return ConfigurationFactory.ParseString("akka.actor.provider = \"Akka.Cluster.ClusterActorRefProvider, Akka.Cluster\"");
        }

        /// <summary>
        /// Checks to see if http://stackoverflow.com/questions/41249465/how-to-test-distributedpubsub-with-the-testkit-in-akka-net is resolved
        /// </summary>
        [Fact]
        public void DistributedPubSubMediator_can_be_activated_in_child_actor()
        {
            EventFilter.Exception<NullReferenceException>().Expect(0, () =>
            {
                var actor = Sys.ActorOf((dsl, context) =>
                {
                    IActorRef mediator = null;
                    dsl.OnPreStart = actorContext =>
                    {
                        mediator = DistributedPubSub.Get(actorContext.System).Mediator;
                    };

                    dsl.Receive<string>(s => s.Equals("check"), (s, actorContext) =>
                    {
                        actorContext.Sender.Tell(mediator);
                    });

                }, "childActor");

                actor.Tell("check");
                var a = ExpectMsg<IActorRef>();
                a.ShouldNotBe(ActorRefs.NoSender);
                a.ShouldNotBe(ActorRefs.Nobody);
            });
        }

        [Fact]
        public async Task DistributedPubSubMediator_should_send_messages_to_dead_letter()
        {
            await EventFilter.DeadLetter<object>().ExpectAsync(10, TimeSpan.FromMinutes(3), () =>
            {
                var mediator = DistributedPubSub.Get(Sys).Mediator;
                var actor = Sys.ActorOf((dsl, context) =>
                {
                }, "childActor");
                mediator.Tell(new Subscribe("pub-sub", actor));
                _ = ExpectMsg<SubscribeAck>();
                mediator.Tell(new Unsubscribe("pub-sub", actor));
                _ = ExpectMsg<UnsubscribeAck>();
                Sys.Scheduler.Advanced.ScheduleOnce(TimeSpan.FromMinutes(2.50), () => 
                { 
                    for(var i = 0; i < 10; i++)
                    {
                        mediator.Tell(new Publish("pub-sub", $"Good {i}"));
                    }
                });

            });            
        }
    }
    public sealed class QueryTopics
    {
        public static QueryTopics Instance = new QueryTopics();
    }

    public sealed class PublishTopic
    {
        public static PublishTopic Instance = new PublishTopic();
    }
}
