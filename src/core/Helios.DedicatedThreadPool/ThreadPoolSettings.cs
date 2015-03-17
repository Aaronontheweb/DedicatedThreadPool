﻿using System;

namespace Helios.Concurrency
{
    /// <summary>
    /// The type of threads to use - either foreground or background threads.
    /// </summary>
    public enum ThreadType
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

        /// <summary>
        /// Default number of milliseconds we use
        /// </summary>
        public const int DefaultQuantumMillis = 30;

        public DedicatedThreadPoolSettings(int numThreads) : this(numThreads, DefaultThreadType, DefaultQuantumMillis) { }

        public DedicatedThreadPoolSettings(int numThreads, int quantum) : this(numThreads, DefaultThreadType, quantum) { }

        public DedicatedThreadPoolSettings(int numThreads, ThreadType threadType, int quantumMillis)
        {
            QuantumMillis = quantumMillis;
            ThreadType = threadType;
            NumThreads = numThreads;
            if(numThreads <= 0) 
                throw new ArgumentOutOfRangeException("numThreads", string.Format("numThreads must be at least 1. Was {0}", numThreads));
            if (quantumMillis <= 0)
                throw new ArgumentOutOfRangeException("quantumMillis", string.Format("quantumMillis must be at least 1. Was {0}", quantumMillis));
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
        /// Minimum run interval for a thread before it's released
        /// </summary>
        public int QuantumMillis { get; private set; }
    }
}
