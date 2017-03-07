// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;

namespace System.Runtime.CompilerServices
{
    /// <summary>Provides an awaiter for a <see cref="SlimTask{TResult}"/>.</summary>
    public struct SlimTaskAwaiter<TResult> : ICriticalNotifyCompletion
    {
        /// <summary>The value being awaited.</summary>
        private readonly SlimTask<TResult> _slimTask;

        /// <summary>Initializes the awaiter.</summary>
        /// <param name="value">The value to be awaited.</param>
        internal SlimTaskAwaiter(SlimTask<TResult> value)
        {
//            Console.WriteLine($"SlimTaskAwaiter ctor called");
            _slimTask = value;
        }

        /// <summary>Gets whether the <see cref="SlimTask{TResult}"/> has completed.</summary>
        public bool IsCompleted
        {
            get
            {
//                Console.WriteLine($"SlimTaskAwaiter.get_IsCompleted called, value = {_slimTask.IsCompleted}");
                return _slimTask.IsCompleted;
            }
        }

        /// <summary>Gets the result of the SlimTask.</summary>
        public TResult GetResult()
        {
//            Console.WriteLine($"SlimTaskAwaiter.GetResult called, value = {_slimTask.Result}");
            return _slimTask.Result;
        }

        /// <summary>Schedules the continuation action for this SlimTask.</summary>
        public void OnCompleted(Action continuation)
        {
            throw new NotImplementedException("SlimTaskAwaiter.OnCompleted");
        }

        /// <summary>Schedules the continuation action for this SlimTask.</summary>
        public void UnsafeOnCompleted(Action continuation)
        {
//            Console.WriteLine($"SlimTaskAwaiter.UnsafeOnCompleted called");
            _slimTask.UnsafeOnCompleted(continuation);
        }

        // Helper for calling from sync code
        public void Wait()
        {
            if (!IsCompleted)
            {
                var mres = new ManualResetEventSlim();
                UnsafeOnCompleted(() => mres.Set());
                mres.Wait();
            }
        }
    }

    /// <summary>Provides an awaiter for a <see cref="SlimTask{TResult}"/>.</summary>
    public struct SlimTaskAwaiter : ICriticalNotifyCompletion
    {
        private readonly SlimTask _slimTask;

        internal SlimTaskAwaiter(SlimTask value)
        {
            _slimTask = value;
        }

        public bool IsCompleted
        {
            get
            {
                return _slimTask.IsCompleted;
            }
        }

        /// <summary>Gets the result of the SlimTask.</summary>
        public void GetResult()
        {
            _slimTask.GetResult();
        }

        /// <summary>Schedules the continuation action for this SlimTask.</summary>
        public void OnCompleted(Action continuation)
        {
            throw new NotImplementedException("SlimTaskAwaiter.OnCompleted");
        }

        /// <summary>Schedules the continuation action for this SlimTask.</summary>
        public void UnsafeOnCompleted(Action continuation)
        {
            _slimTask.UnsafeOnCompleted(continuation);
        }

        // Helper for calling from sync code
        public void Wait()
        {
            if (!IsCompleted)
            {
                var mres = new ManualResetEventSlim();
                UnsafeOnCompleted(() => mres.Set());
                mres.Wait();
            }
        }
    }
}
