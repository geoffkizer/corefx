// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Threading.Tasks
{
    [AsyncMethodBuilder(typeof(AsyncSlimTaskMethodBuilder<>))]
    [StructLayout(LayoutKind.Auto)]
    public struct SlimTask<TResult> : IEquatable<SlimTask<TResult>>
    {
        internal readonly SlimTaskState<TResult> _state;
        internal readonly TResult _result;

        /// <summary>Initialize the <see cref="SlimTask{TResult}"/> with the result of the successful operation.</summary>
        /// <param name="result">The result.</param>
        public SlimTask(TResult result)
        {
            _state = null;
            _result = result;
        }

        /// <summary>
        /// Initialize the <see cref="SlimTask{TResult}"/> with a <see cref="Task{TResult}"/> that represents the operation.
        /// </summary>
        /// <param name="task">The task.</param>
        internal SlimTask(SlimTaskState<TResult> state)
        {
            Debug.Assert(state != null);

            _state = state;
            _result = default(TResult);
        }

        /// <summary>Returns the hash code for this instance.</summary>
        public override int GetHashCode()
        {
            return
                _state != null ? _state.GetHashCode() :
                _result != null ? _result.GetHashCode() :
                0;
        }

        /// <summary>Returns a value indicating whether this value is equal to a specified <see cref="object"/>.</summary>
        public override bool Equals(object obj)
        {
            return
                obj is SlimTask<TResult> &&
                Equals((SlimTask<TResult>)obj);
        }

        /// <summary>Returns a value indicating whether this value is equal to a specified <see cref="SlimTask{TResult}"/> value.</summary>
        public bool Equals(SlimTask<TResult> other)
        {
            return _state != null || other._state != null ?
                _state == other._state :
                EqualityComparer<TResult>.Default.Equals(_result, other._result);
        }

        /// <summary>Returns a value indicating whether two <see cref="SlimTask{TResult}"/> values are equal.</summary>
        public static bool operator ==(SlimTask<TResult> left, SlimTask<TResult> right)
        {
            return left.Equals(right);
        }

        /// <summary>Returns a value indicating whether two <see cref="SlimTask{TResult}"/> values are not equal.</summary>
        public static bool operator !=(SlimTask<TResult> left, SlimTask<TResult> right)
        {
            return !left.Equals(right);
        }

        /// <summary>Gets whether the <see cref="SlimTask{TResult}"/> represents a completed operation.</summary>
        public bool IsCompleted { get { return _state == null || _state.IsCompleted; } }

        /// <summary>Gets the result.</summary>
        public TResult Result { get { return _state == null ? _result : _state.GetResult(); } }

        /// <summary>Gets an awaiter for this value.</summary>
        public SlimTaskAwaiter<TResult> GetAwaiter()
        {
            return new SlimTaskAwaiter<TResult>(this);
        }

        /// <summary>Gets a string-representation of this <see cref="SlimTask{TResult}"/>.</summary>
        public override string ToString()
        {
            if (_state == null)
            {
                return (_result == null ? string.Empty : _result.ToString());
            }

            return _state.ToString();
        }

        // TODO: Remove CreateAsyncMethodBuilder once the C# compiler relies on the AsyncBuilder attribute.

        /// <summary>Creates a method builder for use with an async method.</summary>
        /// <returns>The created builder.</returns>
        [EditorBrowsable(EditorBrowsableState.Never)] // intended only for compiler consumption
        public static AsyncSlimTaskMethodBuilder<TResult> CreateAsyncMethodBuilder() => AsyncSlimTaskMethodBuilder<TResult>.Create();

        public void UnsafeOnCompleted(Action continuation)
        {
            Debug.Assert(_state != null);
            _state.UnsafeOnCompleted(continuation);
        }
    }

    internal class SlimTaskState<TResult>
    {
        private Action _continuation;
        private TResult _result;
        private Exception _exception;

        private static readonly Action s_completedSentinel = () => { };

        public bool IsCompleted { get { return _continuation == s_completedSentinel; } }

        internal TResult GetResult()
        {
            Debug.Assert(_continuation == s_completedSentinel);

            if (_exception == null)
            {
                return _result;
            }
            else
            {
                // TODO: Not right
                throw _exception;
            }
        }

        public void SetException(Exception exception)
        {
            _exception = exception;

            Action continuation = Interlocked.Exchange(ref _continuation, s_completedSentinel);
            if (continuation != null)
            {
                continuation();
            }
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            if (Interlocked.CompareExchange(ref _continuation, continuation, null) == s_completedSentinel)
            {
                continuation();
            }
        }

        public void SetResult(TResult result)
        {
            _result = result;

            Action continuation = Interlocked.Exchange(ref _continuation, s_completedSentinel);
            if (continuation != null)
            {
                continuation();
            }
        }

        public override string ToString()
        {
            if (_continuation == s_completedSentinel)
            {
                return (_exception == null ?
                            (_result == null ? string.Empty : _result.ToString()) :
                            _exception.ToString());
            }

            return "<pending>";
        }
    }

    internal class SlimTaskStateWithStateMachine<TResult, TStateMachine> : SlimTaskState<TResult>//, IAsyncStateMachine
        where TStateMachine : IAsyncStateMachine
    {
        private TStateMachine _stateMachine;

        public SlimTaskStateWithStateMachine()
        {
            _stateMachine = default(TStateMachine);
        }

        public void CopyStateMachine(ref TStateMachine stateMachine)
        {
            _stateMachine = stateMachine;
        }

        public void MoveNext()
        {
            _stateMachine.MoveNext();
        }
    }

    internal struct VoidTaskResult { }

    [AsyncMethodBuilder(typeof(AsyncSlimTaskMethodBuilder))]
    [StructLayout(LayoutKind.Auto)]
    public struct SlimTask : IEquatable<SlimTask>
    {
        internal readonly SlimTaskState<VoidTaskResult> _state;

        /// <summary>
        /// Initialize the <see cref="SlimTask{TResult}"/> with a <see cref="Task{TResult}"/> that represents the operation.
        /// </summary>
        /// <param name="task">The task.</param>
        internal SlimTask(SlimTaskState<VoidTaskResult> state)
        {
            Debug.Assert(state != null);

            _state = state;
        }

        /// <summary>Returns the hash code for this instance.</summary>
        public override int GetHashCode()
        {
            return _state != null ? _state.GetHashCode() : 0;
        }

        /// <summary>Returns a value indicating whether this value is equal to a specified <see cref="object"/>.</summary>
        public override bool Equals(object obj)
        {
            return
                obj is SlimTask &&
                Equals((SlimTask)obj);
        }

        /// <summary>Returns a value indicating whether this value is equal to a specified <see cref="SlimTask{TResult}"/> value.</summary>
        public bool Equals(SlimTask other)
        {
            return _state != null || other._state != null ?
                _state == other._state : true;
        }

        /// <summary>Returns a value indicating whether two <see cref="SlimTask{TResult}"/> values are equal.</summary>
        public static bool operator ==(SlimTask left, SlimTask right)
        {
            return left.Equals(right);
        }

        /// <summary>Returns a value indicating whether two <see cref="SlimTask{TResult}"/> values are not equal.</summary>
        public static bool operator !=(SlimTask left, SlimTask right)
        {
            return !left.Equals(right);
        }

        /// <summary>Gets whether the <see cref="SlimTask{TResult}"/> represents a completed operation.</summary>
        public bool IsCompleted { get { return _state == null || _state.IsCompleted; } }

        /// <summary>Gets the result.</summary>
        public void GetResult()
        {
            if (_state != null)
            {
                _state.GetResult();
            }
        }

        /// <summary>Gets an awaiter for this value.</summary>
        public SlimTaskAwaiter GetAwaiter()
        {
            return new SlimTaskAwaiter(this);
        }

        /// <summary>Gets a string-representation of this <see cref="SlimTask{TResult}"/>.</summary>
        public override string ToString()
        {
            if (_state == null)
            {
                return string.Empty;
            }

            return _state.ToString();
        }

        // TODO: Remove CreateAsyncMethodBuilder once the C# compiler relies on the AsyncBuilder attribute.

        /// <summary>Creates a method builder for use with an async method.</summary>
        /// <returns>The created builder.</returns>
        [EditorBrowsable(EditorBrowsableState.Never)] // intended only for compiler consumption
        public static AsyncSlimTaskMethodBuilder CreateAsyncMethodBuilder() => AsyncSlimTaskMethodBuilder.Create();

        public void UnsafeOnCompleted(Action continuation)
        {
            Debug.Assert(_state != null);
            _state.UnsafeOnCompleted(continuation);
        }
    }
}
