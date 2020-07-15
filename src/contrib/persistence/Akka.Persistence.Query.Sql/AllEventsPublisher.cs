﻿using System;
using System.Collections.Generic;
using System.Text;
using Akka.Actor;
using Akka.Event;
using Akka.Persistence.Sql.Common.Journal;
using Akka.Streams.Actors;

namespace Akka.Persistence.Query.Sql
{
    internal sealed class AllEventsPublisher : ActorPublisher<EventEnvelope>
    {
        [Serializable]
        public sealed class Continue
        {
            public static readonly Continue Instance = new Continue();

            private Continue() { }
        }

        public static Props Props(long fromOffset, int maxBufferSize, string writeJournalPluginId)
            => Actor.Props.Create(() => new AllEventsPublisher(fromOffset, maxBufferSize, writeJournalPluginId));

        private readonly ILoggingAdapter _log;

        private readonly DeliveryBuffer<EventEnvelope> _buffer;
        private readonly IActorRef _journalRef;
        private readonly int _maxBufferSize;
        private bool _completed = false;

        private readonly long _fromOffset;
        private long _currentOffset;

        public AllEventsPublisher(long fromOffset, int maxBufferSize, string writeJournalPluginId)
        {
            _currentOffset = _fromOffset = fromOffset;
            _maxBufferSize = maxBufferSize;
            _buffer = new DeliveryBuffer<EventEnvelope>(OnNext);
            _journalRef = Persistence.Instance.Apply(Context.System).JournalFor(writeJournalPluginId);

            _log = Context.GetLogger();
        }

        private bool IsTimeForReplay => (_buffer.IsEmpty || _buffer.Length <= _maxBufferSize / 2) && !_completed;

        protected override bool Receive(object message) => message.Match()
            .With<Request>(_ => Replay())
            .With<Continue>(() => { })
            .With<Cancel>(_ => Context.Stop(Self))
            .WasHandled;

        private bool Idle(object message) => message.Match()
            .With<Continue>(() =>
            {
                if (IsTimeForReplay) Replay();
            })
            .With<TaggedEventAppended>(() =>
            {
                if (IsTimeForReplay) Replay();
            })
            .With<Request>(ReceiveIdleRequest)
            .With<Cancel>(() => Context.Stop(Self))
            .WasHandled;

        private void Replay()
        {
            var limit = _maxBufferSize - _buffer.Length;
            _log.Debug("replay all events request from [{0}], limit [{1}]", _currentOffset, limit);
            _journalRef.Tell(new ReplayAllEvents(_currentOffset, limit, Self));
            Context.Become(Replaying());
        }

        private Receive Replaying()
        {
            return message => message.Match()
                .With<ReplayedEvent>(replayed =>
                {
                    _buffer.Add(new EventEnvelope(
                        offset: new Sequence(replayed.Offset),
                        persistenceId: replayed.Persistent.PersistenceId,
                        sequenceNr: replayed.Persistent.SequenceNr,
                        @event: replayed.Persistent.Payload));

                    _currentOffset = replayed.Offset;
                    _buffer.DeliverBuffer(TotalDemand);
                })
                .With<EventReplaySuccess>(success =>
                {
                    _log.Debug("event replay completed, currOffset [{0}]", _currentOffset);
                    ReceiveRecoverySuccess(success.HighestSequenceNr);
                })
                .With<EventReplayFailure>(failure =>
                {
                    _log.Debug("event replay failed, due to [{0}]", failure.Cause.Message);
                    _buffer.DeliverBuffer(TotalDemand);
                    OnErrorThenStop(failure.Cause);
                })
                .With<ReplayedAllEvents>(complete =>
                {
                    _completed = true;
                    if (_buffer.IsEmpty)
                        OnCompleteThenStop();

                    _buffer.DeliverBuffer(TotalDemand);
                })
                .With<Request>(_ => _buffer.DeliverBuffer(TotalDemand))
                .With<Continue>(() => { })
                .With<Cancel>(() => Context.Stop(Self))
                .WasHandled;
        }

        private void ReceiveIdleRequest()
        {
            _buffer.DeliverBuffer(TotalDemand);
            if (_buffer.IsEmpty && _completed)
                OnCompleteThenStop();
            else
                Self.Tell(Continue.Instance);
        }

        private void ReceiveRecoverySuccess(long highestSequenceNr)
        {
            _buffer.DeliverBuffer(TotalDemand);
            if (_buffer.IsEmpty && _completed)
                OnCompleteThenStop();
            else
                Self.Tell(Continue.Instance);

            Context.Become(Idle);
        }
    }

}
