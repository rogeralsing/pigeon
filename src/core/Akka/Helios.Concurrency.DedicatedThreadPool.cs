﻿//-----------------------------------------------------------------------
// <copyright file="Helios.Concurrency.DedicatedThreadPool.cs" company="Akka.NET Project">
//     Copyright (C) 2009-2021 Lightbend Inc. <http://www.lightbend.com>
//     Copyright (C) 2013-2021 .NET Foundation <https://github.com/akkadotnet/akka.net>
// </copyright>
//-----------------------------------------------------------------------

/*
 * Copyright 2015 Roger Alsing, Aaron Stannard
 * Helios.DedicatedThreadPool - https://github.com/helios-io/DedicatedThreadPool
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Akka.Dispatch;
using Akka.Util;

namespace Helios.Concurrency
{
    /// <summary>
    /// The type of threads to use - either foreground or background threads.
    /// </summary>
    internal enum ThreadType
    {
        Foreground,
        Background
    }

    /// <summary>
    /// Provides settings for a dedicated thread pool
    /// </summary>
    internal class DedicatedThreadPoolSettings
    {
        /// <summary>
        /// Background threads are the default thread type
        /// </summary>
        public const ThreadType DefaultThreadType = ThreadType.Background;

        public DedicatedThreadPoolSettings(int numThreads, string name = null, TimeSpan? deadlockTimeout = null)
            : this(numThreads, DefaultThreadType, name, deadlockTimeout)
        { }

        public DedicatedThreadPoolSettings(
            int numThreads, 
            ThreadType threadType, 
            string name = null, 
            TimeSpan? deadlockTimeout = null)
        {
            Name = name ?? ("DedicatedThreadPool-" + Guid.NewGuid());
            ThreadType = threadType;
            NumThreads = numThreads;
            DeadlockTimeout = deadlockTimeout;
            if (deadlockTimeout.HasValue && deadlockTimeout.Value.TotalMilliseconds <= 0)
                throw new ArgumentOutOfRangeException("deadlockTimeout", string.Format("deadlockTimeout must be null or at least 1ms. Was {0}.", deadlockTimeout));
            if (numThreads <= 0)
                throw new ArgumentOutOfRangeException("numThreads", string.Format("numThreads must be at least 1. Was {0}", numThreads));
        }

        /// <summary>
        /// The total number of threads to run in this thread pool.
        /// </summary>
        public int NumThreads { get; private set; }

        /// <summary>
        /// The type of threads to run in this thread pool.
        /// </summary>
        public ThreadType ThreadType { get; private set; }

        /// <summary>
        /// Interval to check for thread deadlocks.
        ///
        /// If a thread takes longer than <see cref="DeadlockTimeout"/> it will be aborted
        /// and replaced.
        /// </summary>
        public TimeSpan? DeadlockTimeout { get; private set; }

        /// <summary>
        /// TBD
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// TBD
        /// </summary>
        public Action<Exception> ExceptionHandler { get; private set; }

        /// <summary>
        /// Gets the thread stack size, 0 represents the default stack size.
        /// </summary>
        public int ThreadMaxStackSize { get; private set; }
    }

    /// <summary>
    /// TaskScheduler for working with a <see cref="DedicatedThreadPool"/> instance
    /// </summary>
    internal class DedicatedThreadPoolTaskScheduler : TaskScheduler
    {
        // Indicates whether the current thread is processing work items.
        [ThreadStatic]
        private static bool _currentThreadIsRunningTasks;

        /// <summary>
        /// Number of tasks currently running
        /// </summary>
        private volatile int _parallelWorkers = 0;

        private readonly LinkedList<Task> _tasks = new LinkedList<Task>();

        private readonly DedicatedThreadPool _pool;

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="pool">TBD</param>
        public DedicatedThreadPoolTaskScheduler(DedicatedThreadPool pool)
        {
            _pool = pool;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="task">TBD</param>
        protected override void QueueTask(Task task)
        {
            lock (_tasks)
            {
                _tasks.AddLast(task);
            }

            EnsureWorkerRequested();
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="task">TBD</param>
        /// <param name="taskWasPreviouslyQueued">TBD</param>
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            //current thread isn't running any tasks, can't execute inline
            if (!_currentThreadIsRunningTasks) return false;

            //remove the task from the queue if it was previously added
            if (taskWasPreviouslyQueued)
                if (TryDequeue(task))
                    return TryExecuteTask(task);
                else
                    return false;
            return TryExecuteTask(task);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="task">TBD</param>
        /// <returns>TBD</returns>
        protected override bool TryDequeue(Task task)
        {
            lock (_tasks) return _tasks.Remove(task);
        }

        /// <summary>
        /// Level of concurrency is directly equal to the number of threads
        /// in the <see cref="DedicatedThreadPool"/>.
        /// </summary>
        public override int MaximumConcurrencyLevel
        {
            get { return _pool.Settings.NumThreads; }
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <exception cref="NotSupportedException">
        /// This exception is thrown if can't ensure a thread-safe return of the list of tasks.
        /// </exception>
        /// <returns>TBD</returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            var lockTaken = false;
            try
            {
                Monitor.TryEnter(_tasks, ref lockTaken);

                //should this be immutable?
                if (lockTaken) return _tasks;
                else throw new NotSupportedException();
            }
            finally
            {
                if (lockTaken) Monitor.Exit(_tasks);
            }
        }

        private void EnsureWorkerRequested()
        {
            var count = _parallelWorkers;
            while (count < _pool.Settings.NumThreads)
            {
                var prev = Interlocked.CompareExchange(ref _parallelWorkers, count + 1, count);
                if (prev == count)
                {
                    RequestWorker();
                    break;
                }
                count = prev;
            }
        }

        private void ReleaseWorker()
        {
            var count = _parallelWorkers;
            while (count > 0)
            {
                var prev = Interlocked.CompareExchange(ref _parallelWorkers, count - 1, count);
                if (prev == count)
                {
                    break;
                }
                count = prev;
            }
        }

        private void RequestWorker()
        {
            _pool.QueueUserWorkItem((t) =>
            {
                // this thread is now available for inlining
                _currentThreadIsRunningTasks = true;
                try
                {
                    // Process all available items in the queue.
                    while (true)
                    {
                        Task item;
                        lock (_tasks)
                        {
                            // done processing
                            if (_tasks.Count == 0)
                            {
                                ReleaseWorker();
                                break;
                            }

                            // Get the next item from the queue
                            item = _tasks.First.Value;
                            _tasks.RemoveFirst();
                        }

                        // Execute the task we pulled out of the queue
                        TryExecuteTask(item);
                    }
                }
                // We're done processing items on the current thread
                finally { _currentThreadIsRunningTasks = false; }
            },null);
        }
    }



    /// <summary>
    /// An instanced, dedicated thread pool.
    /// </summary>
    internal sealed class DedicatedThreadPool : IDisposable
    {
        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="settings">TBD</param>
        public DedicatedThreadPool(DedicatedThreadPoolSettings settings)
        {
            _workQueue = new ThreadPoolWorkQueue();
            Settings = settings;
            _workers = Enumerable.Range(1, settings.NumThreads).Select(workerId => new PoolWorker(this, workerId)).ToArray();

            // Note:
            // The DedicatedThreadPoolSupervisor was removed because aborting thread could lead to unexpected behavior
            // If a new implementation is done, it should spawn a new thread when a worker is not making progress and
            // try to keep {settings.NumThreads} active threads.
        }

        /// <summary>
        /// TBD
        /// </summary>
        public DedicatedThreadPoolSettings Settings { get; private set; }

        private readonly ThreadPoolWorkQueue _workQueue;
        private readonly PoolWorker[] _workers;

        /// <summary>
        /// TBD
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// This exception is thrown if the given <paramref name="work"/> item is undefined.
        /// </exception>
        /// <returns>TBD</returns>
        public bool QueueUserWorkItem(Action<IRunnable> work, IRunnable state)
        {
            if (work == null)
                ThrowNullWorkHelper(work);

            return _workQueue.TryAdd(work,state);
        }

        private static void ThrowNullWorkHelper(Action<IRunnable> work)
        {
            throw new ArgumentNullException(nameof(work), "Work item cannot be null.");
        }

        /// <summary>
        /// TBD
        /// </summary>
        public void Dispose()
        {
            _workQueue.CompleteAdding();
        }

        /// <summary>
        /// TBD
        /// </summary>
        public void WaitForThreadsExit()
        {
            WaitForThreadsExit(Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="timeout">TBD</param>
        public void WaitForThreadsExit(TimeSpan timeout)
        {
            Task.WaitAll(_workers.Select(worker => worker.ThreadExit).ToArray(), timeout);
        }

#region Pool worker implementation

        private class PoolWorker
        {
            private readonly DedicatedThreadPool _pool;

            private readonly TaskCompletionSource<object> _threadExit;

            public Task ThreadExit
            {
                get { return _threadExit.Task; }
            }

            public PoolWorker(DedicatedThreadPool pool, int workerId)
            {
                _pool = pool;
                _threadExit = new TaskCompletionSource<object>();

                var thread = new Thread(RunThread)
                {
                    IsBackground = pool.Settings.ThreadType == ThreadType.Background,
                };

                if (pool.Settings.Name != null)
                    thread.Name = string.Format("{0}_{1}", pool.Settings.Name, workerId);

                thread.Start();
            }

            private void RunThread()
            {
                try
                {
                    foreach (var (action,state) in _pool._workQueue.GetConsumingEnumerable())
                    {
                        try
                        {
                            action(state);
                        }
                        catch (Exception ex)
                        {
                            _pool.Settings.ExceptionHandler(ex);
                        }
                    }
                }
                finally
                {
                    _threadExit.TrySetResult(null);
                }
            }
        }

#endregion

#region WorkQueue implementation

        private class ThreadPoolWorkQueue
        {
            private static readonly int ProcessorCount = Environment.ProcessorCount;
            private const int CompletedState = 1;

            private readonly ConcurrentQueue<(Action<IRunnable> act, IRunnable state)>
                _queue = new ConcurrentQueue<(Action<IRunnable> act, IRunnable state)>();
            //private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();
            //private readonly UnfairSemaphore _semaphore = new UnfairSemaphore();
            private readonly UnfairSemaphoreV2 _semaphore = new UnfairSemaphoreV2();
            private int _outstandingRequests;
            private int _isAddingCompleted;

            public bool IsAddingCompleted
            {
                get { return Volatile.Read(ref _isAddingCompleted) == CompletedState; }
            }

            //public bool TryAdd(Action work)
            public bool TryAdd(Action<IRunnable> work, IRunnable state)
            {
                // If TryAdd returns true, it's guaranteed the work item will be executed.
                // If it returns false, it's also guaranteed the work item won't be executed.

                if (IsAddingCompleted)
                    return false;

                _queue.Enqueue((work,state));
                EnsureThreadRequested();

                return true;
            }

            public IEnumerable<(Action<IRunnable> action, IRunnable state)> GetConsumingEnumerable()
            {
                while (true)
                {
                    (Action<IRunnable>action,IRunnable state) work;
                    if (_queue.TryDequeue(out work))
                    {
                        yield return work;
                    }
                    else if (IsAddingCompleted)
                    {
                        while (_queue.TryDequeue(out work))
                            yield return work;

                        break;
                    }
                    else
                    {
                        _semaphore.Wait();
                        MarkThreadRequestSatisfied();
                    }
                }
            }

            public void CompleteAdding()
            {
                int previousCompleted = Interlocked.Exchange(ref _isAddingCompleted, CompletedState);

                if (previousCompleted == CompletedState)
                    return;

                // When CompleteAdding() is called, we fill up the _outstandingRequests and the semaphore
                // This will ensure that all threads will unblock and try to execute the remaining item in
                // the queue. When IsAddingCompleted is set, all threads will exit once the queue is empty.

                while (true)
                {
                    int count = Volatile.Read(ref _outstandingRequests);
                    int countToRelease = UnfairSemaphore.MaxWorker - count;

                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, UnfairSemaphore.MaxWorker, count);

                    if (prev == count)
                    {
                        _semaphore.Release((short)countToRelease);
                        break;
                    }
                }
            }

            private void EnsureThreadRequested()
            {
                // There is a double counter here (_outstandingRequest and _semaphore)
                // Unfair semaphore does not support value bigger than short.MaxValue,
                // trying to Release more than short.MaxValue could fail miserably.

                // The _outstandingRequest counter ensure that we only request a
                // maximum of {ProcessorCount} to the semaphore.

                // It's also more efficient to have two counter, _outstandingRequests is
                // more lightweight than the semaphore.

                // This trick is borrowed from the .Net ThreadPool
                // https://github.com/dotnet/coreclr/blob/bc146608854d1db9cdbcc0b08029a87754e12b49/src/mscorlib/src/System/Threading/ThreadPool.cs#L568

                int count = Volatile.Read(ref _outstandingRequests);
                while (count < ProcessorCount)
                {
                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, count + 1, count);
                    if (prev == count)
                    {
                        _semaphore.Release();
                        break;
                    }
                    count = prev;
                }
            }

            private void MarkThreadRequestSatisfied()
            {
                int count = Volatile.Read(ref _outstandingRequests);
                while (count > 0)
                {
                    int prev = Interlocked.CompareExchange(ref _outstandingRequests, count - 1, count);
                    if (prev == count)
                    {
                        break;
                    }
                    count = prev;
                }
            }
        }

#endregion

#region UnfairSemaphore implementation

        // This class has been translated from:
        // https://github.com/dotnet/coreclr/blob/97433b9d153843492008652ff6b7c3bf4d9ff31c/src/vm/win32threadpool.h#L124

        // UnfairSemaphore is a more scalable semaphore than Semaphore.  It prefers to release threads that have more recently begun waiting,
        // to preserve locality.  Additionally, very recently-waiting threads can be released without an addition kernel transition to unblock
        // them, which reduces latency.
        //
        // UnfairSemaphore is only appropriate in scenarios where the order of unblocking threads is not important, and where threads frequently
        // need to be woken.

        [StructLayout(LayoutKind.Sequential)]
        private sealed class UnfairSemaphore
        {
            public const int MaxWorker = 0x7FFF;

            private static readonly int ProcessorCount = Environment.ProcessorCount;

            // We track everything we care about in a single 64-bit struct to allow us to
            // do CompareExchanges on this for atomic updates.
            [StructLayout(LayoutKind.Explicit)]
            private struct SemaphoreState
            {
                //how many threads are currently spin-waiting for this semaphore?
                [FieldOffset(0)]
                public short Spinners;

                //how much of the semaphore's count is available to spinners?
                [FieldOffset(2)]
                public short CountForSpinners;

                //how many threads are blocked in the OS waiting for this semaphore?
                [FieldOffset(4)]
                public short Waiters;

                //how much count is available to waiters?
                [FieldOffset(6)]
                public short CountForWaiters;

                [FieldOffset(0)]
                public long RawData;
            }

            [StructLayout(LayoutKind.Explicit, Size = 64)]
            private struct CacheLinePadding
            { }

            private readonly Semaphore m_semaphore;

            // padding to ensure we get our own cache line
#pragma warning disable 169
            private readonly CacheLinePadding m_padding1;
            private SemaphoreState m_state;
            private readonly CacheLinePadding m_padding2;
#pragma warning restore 169

            public UnfairSemaphore()
            {
                m_semaphore = new Semaphore(0, short.MaxValue);
            }

            public bool Wait()
            {
                return Wait(Timeout.InfiniteTimeSpan);
            }

            public bool Wait(TimeSpan timeout)
            {
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    // First, just try to grab some count.
                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        // No count available, become a spinner
                        ++newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            break;
                    }
                }

                //
                // Now we're a spinner.
                //
                int numSpins = 0;
                const int spinLimitPerProcessor = 50;
                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    if (currentCounts.CountForSpinners > 0)
                    {
                        --newCounts.CountForSpinners;
                        --newCounts.Spinners;
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        double spinnersPerProcessor = (double)currentCounts.Spinners / ProcessorCount;
                        int spinLimit = (int)((spinLimitPerProcessor / spinnersPerProcessor) + 0.5);
                        if (numSpins >= spinLimit)
                        {
                            --newCounts.Spinners;
                            ++newCounts.Waiters;
                            if (TryUpdateState(newCounts, currentCounts))
                                break;
                        }
                        else
                        {
                            //
                            // We yield to other threads using Thread.Sleep(0) rather than the more traditional Thread.Yield().
                            // This is because Thread.Yield() does not yield to threads currently scheduled to run on other
                            // processors.  On a 4-core machine, for example, this means that Thread.Yield() is only ~25% likely
                            // to yield to the correct thread in some scenarios.
                            // Thread.Sleep(0) has the disadvantage of not yielding to lower-priority threads.  However, this is ok because
                            // once we've called this a few times we'll become a "waiter" and wait on the Semaphore, and that will
                            // yield to anything that is runnable.
                            //
                            Thread.Sleep(0);
                            numSpins++;
                        }
                    }
                }

                //
                // Now we're a waiter
                //
                bool waitSucceeded = m_semaphore.WaitOne(timeout);

                while (true)
                {
                    SemaphoreState currentCounts = GetCurrentState();
                    SemaphoreState newCounts = currentCounts;

                    --newCounts.Waiters;

                    if (waitSucceeded)
                        --newCounts.CountForWaiters;

                    if (TryUpdateState(newCounts, currentCounts))
                        return waitSucceeded;
                }
            }

            public void Release()
            {
                Release(1);
            }

            public void Release(short count)
            {
                while (true)
                {
                    SemaphoreState currentState = GetCurrentState();
                    SemaphoreState newState = currentState;

                    short remainingCount = count;

                    // First, prefer to release existing spinners,
                    // because a) they're hot, and b) we don't need a kernel
                    // transition to release them.
                    short spinnersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Spinners - currentState.CountForSpinners)));
                    newState.CountForSpinners += spinnersToRelease;
                    remainingCount -= spinnersToRelease;

                    // Next, prefer to release existing waiters
                    short waitersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Waiters - currentState.CountForWaiters)));
                    newState.CountForWaiters += waitersToRelease;
                    remainingCount -= waitersToRelease;

                    // Finally, release any future spinners that might come our way
                    newState.CountForSpinners += remainingCount;

                    // Try to commit the transaction
                    if (TryUpdateState(newState, currentState))
                    {
                        // Now we need to release the waiters we promised to release
                        if (waitersToRelease > 0)
                            m_semaphore.Release(waitersToRelease);

                        break;
                    }
                }
            }

            private bool TryUpdateState(SemaphoreState newState, SemaphoreState currentState)
            {
                if (Interlocked.CompareExchange(ref m_state.RawData, newState.RawData, currentState.RawData) == currentState.RawData)
                {
                    Debug.Assert(newState.CountForSpinners <= MaxWorker, "CountForSpinners is greater than MaxWorker");
                    Debug.Assert(newState.CountForSpinners >= 0, "CountForSpinners is lower than zero");
                    Debug.Assert(newState.Spinners <= MaxWorker, "Spinners is greater than MaxWorker");
                    Debug.Assert(newState.Spinners >= 0, "Spinners is lower than zero");
                    Debug.Assert(newState.CountForWaiters <= MaxWorker, "CountForWaiters is greater than MaxWorker");
                    Debug.Assert(newState.CountForWaiters >= 0, "CountForWaiters is lower than zero");
                    Debug.Assert(newState.Waiters <= MaxWorker, "Waiters is greater than MaxWorker");
                    Debug.Assert(newState.Waiters >= 0, "Waiters is lower than zero");
                    Debug.Assert(newState.CountForSpinners + newState.CountForWaiters <= MaxWorker, "CountForSpinners + CountForWaiters is greater than MaxWorker");

                    return true;
                }

                return false;
            }

            private SemaphoreState GetCurrentState()
            {
                // Volatile.Read of a long can get a partial read in x86 but the invalid
                // state will be detected in TryUpdateState with the CompareExchange.

                SemaphoreState state = new SemaphoreState();
                state.RawData = Volatile.Read(ref m_state.RawData);
                return state;
            }
        }
        
        [StructLayout(LayoutKind.Sequential)]
        private sealed class UnfairSemaphoreV2
        {
            public const int MaxWorker = 0x7FFF;

            private static readonly int ProcessorCount = Environment.ProcessorCount;

            // We track everything we care about in a single 64-bit struct to allow us to
            // do CompareExchanges on this for atomic updates.
            private struct SemaphoreStateV2
            {
                private const byte CurrentSpinnerCountShift = 0;
                private const byte CountForSpinnerCountShift = 16;
                private const byte WaiterCountShift = 32;
                private const byte CountForWaiterCountShift = 48;
                
                public long _data;

                private SemaphoreStateV2(ulong data)
                {
                    unchecked
                    {
                        _data = (long)data;    
                    }
                }
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void AddSpinners(ushort value)
                {
                    Debug.Assert(value <= uint.MaxValue - Spinners);
                    unchecked
                    {
                        _data += (long)value << CurrentSpinnerCountShift;    
                    }
                }
                
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void DecrSpinners(ushort value)
                {
                    Debug.Assert(value >= ushort.MinValue + Spinners);
                    unchecked
                    {
                        _data -= (long)value << CurrentSpinnerCountShift;    
                    }
                }

                private uint GetUInt32Value(byte shift) => (uint)(_data >> shift);
                private void SetUInt32Value(uint value, byte shift) 
                {
                    unchecked
                    {
                        _data = (_data & ~((long)uint.MaxValue << shift)) | ((long)value << shift);    
                    }
                    
                }
                    
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private ushort GetUInt16Value(byte shift) => (ushort)(_data >> shift);

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private void SetUInt16Value(ushort value, byte shift)
                {
                    unchecked
                    {
                        _data = (_data & ~((long)ushort.MaxValue << shift)) | ((long)value << shift);    
                    }
                    
                }
                    
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private byte GetByteValue(byte shift) => (byte)(_data >> shift);

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                private void SetByteValue(byte value, byte shift)
                {
                    unchecked
                    {
                        _data = (_data & ~((long)byte.MaxValue << shift)) | ((long)value << shift);    
                    }
                    
                }
                    

                //how many threads are currently spin-waiting for this semaphore?
                public ushort Spinners
                {
                    get { return GetUInt16Value(CurrentSpinnerCountShift); }
                    //set{SetUInt16Value(value,CurrentSpinnerCountShift);}

                }

                //how much of the semaphore's count is available to spinners?
                //[FieldOffset(2)]
                public ushort CountForSpinners
                {
                    get { return GetUInt16Value(CountForSpinnerCountShift); }
                    //set{SetUInt16Value(value,CountForSpinnerCountShift);}
                }
                
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void IncrementCountForSpinners(ushort count)
                {
                    Debug.Assert(CountForSpinners+count < ushort.MaxValue);
                    unchecked
                    {
                        _data += (long)count << CountForSpinnerCountShift;    
                    }
                    
                }

                public void DecrementCountForSpinners()
                {
                    Debug.Assert(CountForSpinners != 0);
                    unchecked
                    {
                        _data -= (long)1 << CountForSpinnerCountShift;    
                    }
                    
                }


                //how many threads are blocked in the OS waiting for this semaphore?
                
                public ushort Waiters
                {
                    get { return GetUInt16Value(WaiterCountShift); }
                    //set{SetUInt16Value(value,WaiterCountShift);}

                }
                
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void AddWaiters(ushort value)
                {
                    Debug.Assert(value <= uint.MaxValue - Waiters);
                    unchecked
                    {
                        _data += (long)value << WaiterCountShift;    
                    }
                }
                
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void DecrWaiters(ushort value)
                {
                    Debug.Assert(value >= ushort.MinValue + Waiters);
                    unchecked
                    {
                        _data -= (long)value << WaiterCountShift;    
                    }
                }
                //how much count is available to waiters?
                public ushort CountForWaiters
                {
                    get { return GetUInt16Value(CountForWaiterCountShift); }
                    //set{SetUInt16Value(value,CountForWaiterCountShift);}
                }
                
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void IncrCountForWaiters(ushort value)
                {
                    Debug.Assert(value <= ushort.MaxValue + CountForWaiters);
                    unchecked
                    {
                        _data += (long)value << CountForWaiterCountShift;    
                    }
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                public void DecrCountForWaiters(ushort value)
                {
                    Debug.Assert(value >= ushort.MinValue + CountForWaiters);
                    unchecked
                    {
                        _data -= (long)value << CountForWaiterCountShift;    
                    }
                }
            }

            [StructLayout(LayoutKind.Explicit, Size = 64)]
            private struct CacheLinePadding
            { }

            private readonly Semaphore m_semaphore;

            // padding to ensure we get our own cache line
#pragma warning disable 169
            private readonly CacheLinePadding m_padding1;
            private SemaphoreStateV2 m_state;
            private readonly CacheLinePadding m_padding2;
#pragma warning restore 169

            public UnfairSemaphoreV2()
            {
                m_semaphore = new Semaphore(0, short.MaxValue);
            }

            public bool Wait()
            {
                return Wait(Timeout.InfiniteTimeSpan);
            }

            public bool Wait(TimeSpan timeout)
            {
                while (true)
                {
                    SemaphoreStateV2 currentCounts = GetCurrentState();
                    SemaphoreStateV2 newCounts = currentCounts;

                    // First, just try to grab some count.
                    if (currentCounts.CountForSpinners > 0)
                    {
                        newCounts.DecrementCountForSpinners();
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        // No count available, become a spinner
                        newCounts.AddSpinners(1);
                        if (TryUpdateState(newCounts, currentCounts))
                            break;
                    }
                }

                //
                // Now we're a spinner.
                //
                int numSpins = 0;
                const int spinLimitPerProcessor = 50;
                while (true)
                {
                    SemaphoreStateV2 currentCounts = GetCurrentState();
                    SemaphoreStateV2 newCounts = currentCounts;

                    if (currentCounts.CountForSpinners > 0)
                    {
                        newCounts.DecrementCountForSpinners();
                        newCounts.DecrSpinners(1);
                        if (TryUpdateState(newCounts, currentCounts))
                            return true;
                    }
                    else
                    {
                        double spinnersPerProcessor = (double)currentCounts.Spinners / ProcessorCount;
                        int spinLimit = (int)((spinLimitPerProcessor / spinnersPerProcessor) + 0.5);
                        if (numSpins >= spinLimit)
                        {
                            newCounts.DecrSpinners(1);
                            newCounts.AddWaiters(1);
                            if (TryUpdateState(newCounts, currentCounts))
                                break;
                        }
                        else
                        {
                            //
                            // We yield to other threads using Thread.Sleep(0) rather than the more traditional Thread.Yield().
                            // This is because Thread.Yield() does not yield to threads currently scheduled to run on other
                            // processors.  On a 4-core machine, for example, this means that Thread.Yield() is only ~25% likely
                            // to yield to the correct thread in some scenarios.
                            // Thread.Sleep(0) has the disadvantage of not yielding to lower-priority threads.  However, this is ok because
                            // once we've called this a few times we'll become a "waiter" and wait on the Semaphore, and that will
                            // yield to anything that is runnable.
                            //
                            Thread.Sleep(0);
                            numSpins++;
                        }
                    }
                }

                //
                // Now we're a waiter
                //
                bool waitSucceeded = m_semaphore.WaitOne(timeout);

                while (true)
                {
                    SemaphoreStateV2 currentCounts = GetCurrentState();
                    SemaphoreStateV2 newCounts = currentCounts;

                    newCounts.DecrWaiters(1);

                    if (waitSucceeded)
                        newCounts.DecrCountForWaiters(1);

                    if (TryUpdateState(newCounts, currentCounts))
                        return waitSucceeded;
                }
            }

            public void Release()
            {
                Release(1);
            }

            public void Release(short count)
            {
                while (true)
                {
                    SemaphoreStateV2 currentState = GetCurrentState();
                    SemaphoreStateV2 newState = currentState;

                    short remainingCount = count;

                    // First, prefer to release existing spinners,
                    // because a) they're hot, and b) we don't need a kernel
                    // transition to release them.
                    short spinnersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Spinners - currentState.CountForSpinners)));
                    newState.IncrementCountForSpinners((ushort)spinnersToRelease);// .CountForSpinners = (ushort)(newState.CountForSpinners + spinnersToRelease);
                    remainingCount -= spinnersToRelease;

                    // Next, prefer to release existing waiters
                    short waitersToRelease = Math.Max((short)0, Math.Min(remainingCount, (short)(currentState.Waiters - currentState.CountForWaiters)));
                    newState.IncrCountForWaiters((ushort)waitersToRelease);// .CountForWaiters = (ushort)(newState.CountForWaiters+ waitersToRelease);
                    remainingCount -= waitersToRelease;

                    // Finally, release any future spinners that might come our way
                    newState.IncrementCountForSpinners((ushort)remainingCount);

                    // Try to commit the transaction
                    if (TryUpdateState(newState, currentState))
                    {
                        // Now we need to release the waiters we promised to release
                        if (waitersToRelease > 0)
                            m_semaphore.Release(waitersToRelease);

                        break;
                    }
                }
            }

            private bool TryUpdateState(SemaphoreStateV2 newState, SemaphoreStateV2 currentState)
            {
                if (Interlocked.CompareExchange(ref m_state._data, newState._data, currentState._data) == currentState._data)
                {
                    Debug.Assert(newState.CountForSpinners <= MaxWorker, "CountForSpinners is greater than MaxWorker");
                    Debug.Assert(newState.CountForSpinners >= 0, "CountForSpinners is lower than zero");
                    Debug.Assert(newState.Spinners <= MaxWorker, "Spinners is greater than MaxWorker");
                    Debug.Assert(newState.Spinners >= 0, "Spinners is lower than zero");
                    Debug.Assert(newState.CountForWaiters <= MaxWorker, "CountForWaiters is greater than MaxWorker");
                    Debug.Assert(newState.CountForWaiters >= 0, "CountForWaiters is lower than zero");
                    Debug.Assert(newState.Waiters <= MaxWorker, "Waiters is greater than MaxWorker");
                    Debug.Assert(newState.Waiters >= 0, "Waiters is lower than zero");
                    Debug.Assert(newState.CountForSpinners + newState.CountForWaiters <= MaxWorker, "CountForSpinners + CountForWaiters is greater than MaxWorker");

                    return true;
                }

                return false;
            }

            private SemaphoreStateV2 GetCurrentState()
            {
                // Volatile.Read of a long can get a partial read in x86 but the invalid
                // state will be detected in TryUpdateState with the CompareExchange.

                SemaphoreStateV2 state = new SemaphoreStateV2();
                state._data = Volatile.Read(ref m_state._data);
                return state;
            }
        }

#endregion
    }
}

