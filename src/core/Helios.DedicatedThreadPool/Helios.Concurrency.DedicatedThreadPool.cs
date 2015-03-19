﻿/*
 * Copyright 2015 Roger Alsing, Aaron Stannard
 * Helios.DedicatedThreadPool - https://github.com/helios-io/DedicatedThreadPool
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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

        public DedicatedThreadPoolSettings(int numThreads) : this(numThreads, DefaultThreadType) { }

        public DedicatedThreadPoolSettings(int numThreads, ThreadType threadType)
        {
            ThreadType = threadType;
            NumThreads = numThreads;
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

        public DedicatedThreadPoolTaskScheduler(DedicatedThreadPool pool)
        {
            _pool = pool;
        }

        protected override void QueueTask(Task task)
        {
            lock (_tasks)
            {
                _tasks.AddLast(task);
            }

            EnsureWorkerRequested();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            //current thread isn't running any tasks, can't execute inline
            if (!_currentThreadIsRunningTasks) return false;

            //remove the task from the queue if it was previously added
            if(taskWasPreviouslyQueued)
                if (TryDequeue(task))
                    return TryExecuteTask(task);
                else
                    return false;
            return TryExecuteTask(task);
        }

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
            _pool.QueueUserWorkItem(() =>
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
            });
        }
    }

    /// <summary>
    /// An instanced, dedicated thread pool.
    /// </summary>
    internal class DedicatedThreadPool : IDisposable
    {
       
        public DedicatedThreadPool(DedicatedThreadPoolSettings settings)
        {
            Settings = settings;
            Workers = Enumerable.Repeat(new WorkerQueue(), settings.NumThreads).ToArray();
            foreach (var worker in Workers)
            {
                new PoolWorker(worker, this);
            }
        }

        public DedicatedThreadPoolSettings Settings { get; private set; }        

        internal volatile bool ShutdownRequested;

        public readonly WorkerQueue[] Workers;

        [ThreadStatic]
        internal static PoolWorker CurrentWorker;

        /// <summary>
        /// index for round-robin load-balancing across worker threads
        /// </summary>
        private volatile int _index = 0;

        public bool WasDisposed { get; private set; }

        private void Shutdown()
        {
            ShutdownRequested = true;
        }

        private void RequestThread(WorkerQueue unclaimedQueue)
        {
            var worker = new PoolWorker(unclaimedQueue, this);
        }

        public bool QueueUserWorkItem(Action work)
        {
            bool success = true;

            //don't queue work if we've been disposed
            if (WasDisposed) return false;

            if (work != null)
            {
                //no local queue, write to a round-robin queue
                //if (null == CurrentWorker)
                //{
                    //using volatile instead of interlocked, no need to be exact, gaining 20% perf
                    unchecked
                    {
                        _index = (_index + 1);
                        Workers[_index & 0x7fffffff  % Settings.NumThreads].AddWork(work);
                    }
                //}
                //else //recursive task queue, write directly
                //{
                //    // send work directly to PoolWorker
                //    // CurrentWorker.AddWork(work);
                //}
            }
            else
            {
                throw new ArgumentNullException("work");
            }
            return success;
        }

        #region IDisposable members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Dispose(bool isDisposing)
        {
            if (!WasDisposed)
            {
                if (isDisposing)
                {
                    Shutdown();
                }
            }

            WasDisposed = true;
        }

        #endregion

        #region Pool worker implementation

        internal sealed class WorkerQueue
        {
            internal ConcurrentQueue<Action> WorkQueue = new ConcurrentQueue<Action>();
            internal readonly ManualResetEventSlim Event = new ManualResetEventSlim(false);

            public void AddWork(Action work)
            {
                WorkQueue.Enqueue(work);
                Event.Set();
            }
        }

        internal class PoolWorker
        {
            private WorkerQueue _work;
            private DedicatedThreadPool _pool;

            private ManualResetEventSlim _event;
            private ConcurrentQueue<Action> _workQueue;

            public PoolWorker(WorkerQueue work, DedicatedThreadPool pool)
            {
                _work = work;
                _pool = pool;
                _event = _work.Event;
                _workQueue = _work.WorkQueue;

                var thread = new Thread(() =>
                {
                    CurrentWorker = this;
                    
                    
                    while (!_pool.ShutdownRequested)
                    {
                        //suspend if no more work is present
                        _event.Wait();
                        
                        Action action;
                        while (_workQueue.TryDequeue(out action))
                        {
                            try
                            {
                                action();
                            }
                            catch (Exception ex)
                            {
                                /* request a new thread then shut down */
                                _pool.RequestThread(_work);
                                CurrentWorker = null;
                                _work = null;
                                _event = null;
                                _workQueue = null;
                                _pool = null;
                                throw;
                            }
                        }
                        if (_workQueue.IsEmpty)
                        {
                            _event.Reset();
                        }
                        if (_workQueue.IsEmpty)
                        {
                            _event.Set();
                        }
                    }
                })
                {
                    IsBackground = _pool.Settings.ThreadType == ThreadType.Background
                };
                thread.Start();
            }
        }

        #endregion
    }
}
