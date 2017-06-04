﻿using Akka.Actor;
using Akka.Configuration;
using Akka.TestKit;
using Akka.TestKit.TestActors;
using Akka.TestKit.Xunit2;
using Xunit;

namespace Akka.Persistence.Tests
{
    public class RecoveryPermitterSpec : PersistenceSpec
    {
        public class TestPersistentActor : UntypedPersistentActor
        {
            public override string PersistenceId { get; }
            public IActorRef Probe { get; }

            public static Props Props(string name, IActorRef probe) =>
                Actor.Props.Create(() => new TestPersistentActor(name, probe));

            public TestPersistentActor(string name, IActorRef probe)
            {
                PersistenceId = name;
                Probe = probe;
            }

            protected override void PostStop()
            {
                base.PostStop();
                Probe.Tell("postStop");
            }

            protected override void OnRecover(object message)
            {
                if (message is RecoveryCompleted)
                    Probe.Tell(message);
            }

            protected override void OnCommand(object message)
            {
                if (message is "stop")
                    Context.Stop(Self);
            }
        }

        private readonly IActorRef permitter;

        public RecoveryPermitterSpec() : base(ConfigurationFactory.ParseString(@"
            akka.persistence.max-concurrent-recoveries = 3
            akka.persistence.journal.plugin = ""akka.persistence.journal.inmem""

            # snapshot store plugin is NOT defined, things should still work
            akka.persistence.snapshot-store.plugin = ""akka.persistence.no-snapshot-store""
            akka.persistence.snapshot-store.local.dir = ""target/snapshots-" + typeof(RecoveryPermitterSpec).FullName +
                                                                               "/"))
        {
            permitter = Persistence.Instance.Apply(Sys).RecoveryPermitter();
        }

        private void RequestPermit(TestProbe probe)
        {
            permitter.Tell(new RequestRecoveryPermit(), probe.Ref);
            probe.ExpectMsg<RecoveryPermitGranted>();
        }

        [Fact]
        public void RecoveryPermitter_must_grant_permits_up_to_the_limit()
        {
            var p1 = new TestProbe(Sys, new XunitAssertions());
            var p2 = new TestProbe(Sys, new XunitAssertions());
            var p3 = new TestProbe(Sys, new XunitAssertions());
            var p4 = new TestProbe(Sys, new XunitAssertions());
            var p5 = new TestProbe(Sys, new XunitAssertions());

            RequestPermit(p1);
            RequestPermit(p2);
            RequestPermit(p3);

            permitter.Tell(new RequestRecoveryPermit(), p4.Ref);
            permitter.Tell(new RequestRecoveryPermit(), p5.Ref);
            p4.ExpectNoMsg(100);
            p5.ExpectNoMsg(10);

            permitter.Tell(new ReturnRecoveryPermit(), p2.Ref);
            p4.ExpectMsg<RecoveryPermitGranted>();
            p5.ExpectNoMsg(100);

            permitter.Tell(new ReturnRecoveryPermit(), p1.Ref);
            p5.ExpectMsg<RecoveryPermitGranted>();

            permitter.Tell(new ReturnRecoveryPermit(), p3.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p4.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p5.Ref);
        }

        [Fact]
        public void RecoveryPermitter_must_grant_recovery_when_all_permits_not_used()
        {
            var p1 = new TestProbe(Sys, new XunitAssertions());
            var p2 = new TestProbe(Sys, new XunitAssertions());

            RequestPermit(p1);

            Sys.ActorOf(TestPersistentActor.Props("p2", p2.Ref));
            p2.ExpectMsg<RecoveryCompleted>();
            permitter.Tell(new ReturnRecoveryPermit(), p1.Ref);
        }

        [Fact]
        public void RecoveryPermitter_must_delay_recovery_when_all_permits_used()
        {
            var p1 = new TestProbe(Sys, new XunitAssertions());
            var p2 = new TestProbe(Sys, new XunitAssertions());
            var p3 = new TestProbe(Sys, new XunitAssertions());
            var p4 = new TestProbe(Sys, new XunitAssertions());

            RequestPermit(p1);
            RequestPermit(p2);
            RequestPermit(p3);

            var persistentActor = Sys.ActorOf(TestPersistentActor.Props("p4", p4.Ref));
            p4.Watch(persistentActor);
            persistentActor.Tell("stop");
            p4.ExpectNoMsg(200);

            permitter.Tell(new ReturnRecoveryPermit(), p3.Ref);

            p4.ExpectMsg<RecoveryCompleted>();
            p4.ExpectMsg("postStop");
            p4.ExpectTerminated(persistentActor);

            permitter.Tell(new ReturnRecoveryPermit(), p1.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p2.Ref);
        }

        [Fact]
        public void RecoveryPermitter_must_return_permit_when_actor_is_prematurely_terminated_before_holding_permit()
        {
            var p1 = new TestProbe(Sys, new XunitAssertions());
            var p2 = new TestProbe(Sys, new XunitAssertions());
            var p3 = new TestProbe(Sys, new XunitAssertions());
            var p4 = new TestProbe(Sys, new XunitAssertions());
            var p5 = new TestProbe(Sys, new XunitAssertions());

            RequestPermit(p1);
            RequestPermit(p2);
            RequestPermit(p3);

            var persistentActor = Sys.ActorOf(TestPersistentActor.Props("p4", p4.Ref));
            p4.ExpectNoMsg(100);

            permitter.Tell(new RequestRecoveryPermit(), p5.Ref);
            p5.ExpectNoMsg(100);

            // PoisonPill is not stashed
            persistentActor.Tell(PoisonPill.Instance);
            p4.ExpectMsg("postStop");

            // persistentActor didn't hold a permit so still
            p5.ExpectNoMsg(100);

            permitter.Tell(new ReturnRecoveryPermit(), p1.Ref);
            p5.ExpectMsg<RecoveryPermitGranted>();

            permitter.Tell(new ReturnRecoveryPermit(), p2.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p3.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p4.Ref);
        }

        [Fact]
        public void RecoveryPermitter_must_return_permit_when_actor_is_prematurely_terminated_when_holding_permit()
        {
            var p1 = new TestProbe(Sys, new XunitAssertions());
            var p2 = new TestProbe(Sys, new XunitAssertions());
            var p3 = new TestProbe(Sys, new XunitAssertions());
            var p4 = new TestProbe(Sys, new XunitAssertions());

            var actor = Sys.ActorOf(ForwardActor.Props(p1.Ref));
            permitter.Tell(new RequestRecoveryPermit(), actor);
            p1.ExpectMsg<RecoveryPermitGranted>();

            RequestPermit(p2);
            RequestPermit(p3);

            permitter.Tell(new RequestRecoveryPermit(), p4.Ref);
            p4.ExpectNoMsg(100);

            actor.Tell(PoisonPill.Instance);
            p4.ExpectMsg<RecoveryPermitGranted>();

            permitter.Tell(new ReturnRecoveryPermit(), p2.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p3.Ref);
            permitter.Tell(new ReturnRecoveryPermit(), p4.Ref);
        }
    }
}
