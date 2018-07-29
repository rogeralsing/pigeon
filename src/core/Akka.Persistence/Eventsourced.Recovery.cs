﻿//-----------------------------------------------------------------------
// <copyright file="Eventsourced.Recovery.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2018 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2018 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Akka.Persistence.Internal;

namespace Akka.Persistence
{
    internal delegate void StateReceive(Receive receive, object message);

    internal class EventsourcedState
    {
        public EventsourcedState(string name, Func<bool> isRecoveryRunning, StateReceive stateReceive)
        {
            Name = name;
            IsRecoveryRunning = isRecoveryRunning;
            StateReceive = stateReceive;
        }

        public string Name { get; }
        public Func<bool> IsRecoveryRunning { get; }
        public StateReceive StateReceive { get; }

        public override string ToString() => Name;
    }

    /// <summary>
    /// TBD
    /// </summary>
    public abstract partial class Eventsourced
    {
        /// <summary>
        /// Initial state. Before starting the actual recovery it must get a permit from the `RecoveryPermitter`.
        /// When starting many persistent actors at the same time the journal and its data store is protected from
        /// being overloaded by limiting number of recoveries that can be in progress at the same time.
        /// When receiving `RecoveryPermitGranted` it switches to `recoveryStarted` state.
        /// All incoming messages are stashed.
        /// </summary>
        private EventsourcedState WaitingRecoveryPermit(Recovery recovery)
        {
            return new EventsourcedState("waiting for recovery permit", () => true, (receive, message) =>
            {
                if (message is RecoveryPermitGranted)
                    StartRecovery(recovery);
                else
                    StashInternally(message);
            });
        }

        /// <summary>
        /// Processes a loaded snapshot, if any. A loaded snapshot is offered with a <see cref="SnapshotOffer"/>
        /// message to the actor's <see cref="ReceiveRecover"/>. Then initiates a message replay, either starting
        /// from the loaded snapshot or from scratch, and switches to <see cref="RecoveryStarted"/> state.
        /// All incoming messages are stashed.
        /// </summary>
        /// <param name="maxReplays">Maximum number of messages to replay</param>
        private EventsourcedState RecoveryStarted(long maxReplays)
        {
            // protect against snapshot stalling forever because journal overloaded and such
            var timeout = Extension.JournalConfigFor(JournalPluginId).GetTimeSpan("recovery-event-timeout", null, false);
            var timeoutCancelable = Context.System.Scheduler.ScheduleTellOnceCancelable(timeout, Self, new RecoveryTick(true), Self);

            Receive recoveryBehavior = message =>
            {
                Receive receiveRecover = ReceiveRecover;
                if (message is IPersistentRepresentation && IsRecovering)
                    return receiveRecover((message as IPersistentRepresentation).Payload);
                else if (message is SnapshotOffer)
                    return receiveRecover((SnapshotOffer)message);
                else if (message is RecoveryCompleted)
                    return receiveRecover(RecoveryCompleted.Instance);
                else return false;
            };

            return new EventsourcedState("recovery started - replay max: " + maxReplays, () => true, (receive, message) =>
            {
                try
                {
                    if (message is LoadSnapshotResult res)
                    {
                        timeoutCancelable.Cancel();
                        if (res.Snapshot != null)
                        {
                            var offer = new SnapshotOffer(res.Snapshot.Metadata, res.Snapshot.Snapshot);
                            var seqNr = LastSequenceNr;
                            LastSequenceNr = res.Snapshot.Metadata.SequenceNr;
                            if (!base.AroundReceive(recoveryBehavior, offer))
                            {
                                LastSequenceNr = seqNr;
                                Unhandled(offer);
                            }
                        }

                        ChangeState(Recovering(recoveryBehavior, timeout));
                        Journal.Tell(new ReplayMessages(LastSequenceNr + 1L, res.ToSequenceNr, maxReplays, PersistenceId, Self));
                    }
                    else if (message is LoadSnapshotFailed failed)
                    {
                        timeoutCancelable.Cancel();
                        try
                        {
                            OnRecoveryFailure(failed.Cause);
                        }
                        finally
                        {
                            Context.Stop(Self);
                        }
                        ReturnRecoveryPermit();
                    }
                    else if (message is RecoveryTick tick && tick.Snapshot)
                    {
                        try
                        {
                            OnRecoveryFailure(
                                new RecoveryTimedOutException(
                                    $"Recovery timed out, didn't get snapshot within {timeout.TotalSeconds}s."));
                        }
                        finally
                        {
                            Context.Stop(Self);
                        }
                        ReturnRecoveryPermit();
                    }
                    else
                    {
                        StashInternally(message);
                    }
                }
                catch (Exception)
                {
                    ReturnRecoveryPermit();
                    throw;
                }
            });
        }

        /// <summary>
        /// Processes replayed messages, if any. The actor's <see cref="ReceiveRecover"/> is invoked with the replayed events.
        ///
        /// If replay succeeds it got highest stored sequence number response from the journal and then switches
        /// to <see cref="ProcessingCommands"/> state.
        /// If replay succeeds the <see cref="OnReplaySuccess"/> callback method is called, otherwise
        /// <see cref="OnRecoveryFailure"/>.
        ///
        /// All incoming messages are stashed.
        /// </summary>
        private EventsourcedState Recovering(Receive recoveryBehavior, TimeSpan timeout)
        {
            // protect against event replay stalling forever because of journal overloaded and such
            var timeoutCancelable = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(timeout, timeout, Self, new RecoveryTick(false), Self);
            var eventSeenInInterval = false;
            var recoveryRunning = true;

            return new EventsourcedState("replay started", () => recoveryRunning, (receive, message) =>
            {
                try
                {
                    switch (message)
                    {
                        case ReplayedMessage replayed:
                            try
                            {
                                eventSeenInInterval = true;
                                UpdateLastSequenceNr(replayed.Persistent);
                                base.AroundReceive(recoveryBehavior, replayed.Persistent);
                            }
                            catch (Exception cause)
                            {
                                timeoutCancelable.Cancel();
                                try
                                {
                                    OnRecoveryFailure(cause, replayed.Persistent.Payload);
                                }
                                finally
                                {
                                    Context.Stop(Self);
                                }
                                ReturnRecoveryPermit();
                            }
                            break;
                        case RecoverySuccess success:
                            timeoutCancelable.Cancel();
                            OnReplaySuccess();
                            _sequenceNr = success.HighestSequenceNr;
                            LastSequenceNr = success.HighestSequenceNr;
                            recoveryRunning = false;
                            try
                            {
                                base.AroundReceive(recoveryBehavior, RecoveryCompleted.Instance);
                            }
                            finally
                            {
                                // in finally in case exception and resume strategy
                                TransitToProcessingState();
                            }
                            ReturnRecoveryPermit();
                            break;
                        case ReplayMessagesFailure failure:
                            timeoutCancelable.Cancel();
                            try
                            {
                                OnRecoveryFailure(failure.Cause);
                            }
                            finally
                            {
                                Context.Stop(Self);
                            }
                            ReturnRecoveryPermit();
                            break;
                        case RecoveryTick tick when !tick.Snapshot:
                            if (!eventSeenInInterval)
                            {
                                timeoutCancelable.Cancel();
                                try
                                {
                                    OnRecoveryFailure(
                                        new RecoveryTimedOutException(
                                            $"Recovery timed out, didn't get event within {timeout.TotalSeconds}s, highest sequence number seen {LastSequenceNr}."));
                                }
                                finally
                                {
                                    Context.Stop(Self);
                                }
                                ReturnRecoveryPermit();
                            }
                            else
                            {
                                eventSeenInInterval = false;
                            }
                            break;
                        default:
                            StashInternally(message);
                            break;
                    }
                }
                catch (Exception)
                {
                    ReturnRecoveryPermit();
                    throw;
                }
            });
        }

        private void ReturnRecoveryPermit() =>
            Extension.RecoveryPermitter().Tell(Akka.Persistence.ReturnRecoveryPermit.Instance, Self);

        private void TransitToProcessingState()
        {
            if (_eventBatch.Count > 0) FlushBatch();

            if (_pendingStashingPersistInvocations > 0)
            {
                ChangeState(PersistingEvents());
            }
            else
            {
                ChangeState(ProcessingCommands());
                _internalStash.UnstashAll();
            }
        }

        /// <summary>
        /// Command processing state. If event persistence is pending after processing a command, event persistence
        /// is triggered and the state changes to <see cref="PersistingEvents"/>.
        /// </summary>
        private EventsourcedState ProcessingCommands()
        {
            return new EventsourcedState("processing commands", () => false, (receive, message) =>
            {
                var handled = CommonProcessingStateBehavior(message, err =>
                {
                    _pendingInvocations.Pop();
                    UnstashInternally(err);
                });
                if (!handled)
                {
                    try
                    {
                        base.AroundReceive(receive, message);
                        OnProcessingCommandsAroundReceiveComplete(false);
                    }
                    catch (Exception)
                    {
                        OnProcessingCommandsAroundReceiveComplete(true);
                        throw;
                    }
                }
            });
        }

        private void OnProcessingCommandsAroundReceiveComplete(bool err)
        {
            if (_eventBatch.Count > 0) FlushBatch();

            if (_asyncTaskRunning)
            {
                //do nothing, wait for the task to finish
            }
            else if (_pendingStashingPersistInvocations > 0)
                ChangeState(PersistingEvents());
            else
                UnstashInternally(err);
        }

        private void FlushBatch()
        {
            if (_eventBatch.Count > 0)
            {
                foreach (var p in _eventBatch.Reverse())
                {
                    _journalBatch.Add(p);
                }
                _eventBatch = new LinkedList<IPersistentEnvelope>();
            }

            FlushJournalBatch();
        }

        /// <summary>
        /// Event persisting state. Remains until pending events are persisted and then changes state to <see cref="ProcessingCommands"/>.
        /// Only events to be persisted are processed. All other messages are stashed internally.
        /// </summary>
        private EventsourcedState PersistingEvents()
        {
            return new EventsourcedState("persisting events", () => false, (receive, message) =>
            {
                var handled = CommonProcessingStateBehavior(message, err =>
                {
                    var invocation = _pendingInvocations.Pop();

                    // enables an early return to `processingCommands`, because if this counter hits `0`,
                    // we know the remaining pendingInvocations are all `persistAsync` created, which
                    // means we can go back to processing commands also - and these callbacks will be called as soon as possible
                    if (invocation is StashingHandlerInvocation)
                        _pendingStashingPersistInvocations--;

                    if (_pendingStashingPersistInvocations == 0)
                    {
                        ChangeState(ProcessingCommands());
                        UnstashInternally(err);
                    }
                });

                if (!handled)
                    StashInternally(message);
            });
        }

        private void PeekApplyHandler(object payload)
        {
            try
            {
                _pendingInvocations.First.Value.Handler(payload);
            }
            finally
            {
                FlushBatch();
            }
        }

        private bool CommonProcessingStateBehavior(object message, Action<bool> onWriteMessageComplete)
        {
            // _instanceId mismatch can happen for persistAsync and defer in case of actor restart
            // while message is in flight, in that case we ignore the call to the handler
            if (message is WriteMessageSuccess)
            {
                var m = (WriteMessageSuccess)message;
                if (m.ActorInstanceId == _instanceId)
                {
                    UpdateLastSequenceNr(m.Persistent);
                    try
                    {
                        PeekApplyHandler(m.Persistent.Payload);
                        onWriteMessageComplete(false);
                    }
                    catch
                    {
                        onWriteMessageComplete(true);
                        throw;
                    }
                }
            }
            else if (message is WriteMessageRejected)
            {
                var m = (WriteMessageRejected)message;
                if (m.ActorInstanceId == _instanceId)
                {
                    var p = m.Persistent;
                    UpdateLastSequenceNr(p);
                    onWriteMessageComplete(false);
                    OnPersistRejected(m.Cause, p.Payload, p.SequenceNr);
                }
            }
            else if (message is WriteMessageFailure)
            {
                var m = (WriteMessageFailure)message;
                if (m.ActorInstanceId == _instanceId)
                {
                    var p = m.Persistent;
                    onWriteMessageComplete(false);
                    try
                    {
                        OnPersistFailure(m.Cause, p.Payload, p.SequenceNr);
                    }
                    finally
                    {
                        Context.Stop(Self);
                    }
                }
            }
            else if (message is LoopMessageSuccess)
            {
                var m = (LoopMessageSuccess)message;
                if (m.ActorInstanceId == _instanceId)
                {
                    try
                    {
                        PeekApplyHandler(m.Message);
                        onWriteMessageComplete(false);
                    }
                    catch (Exception)
                    {
                        onWriteMessageComplete(true);
                        throw;
                    }
                }
            }
            else if (message is WriteMessagesSuccessful)
            {
                _isWriteInProgress = false;
                FlushJournalBatch();
            }
            else if (message is WriteMessagesFailed)
            {
                _isWriteInProgress = false;
                // it will be stopped by the first WriteMessageFailure message
            }
            else if (message is RecoveryTick)
            {
                // we may have one of these in the mailbox before the scheduled timeout
                // is cancelled when recovery has completed, just consume it so the concrete actor never sees it
            }
            else return false;
            return true;
        }
    }
}
