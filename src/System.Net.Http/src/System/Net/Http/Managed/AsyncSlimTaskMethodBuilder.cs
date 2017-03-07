// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    /// <summary>Represents a builder for asynchronous methods that returns a <see cref="SlimTask{TResult}"/>.</summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    [StructLayout(LayoutKind.Auto)]
    public struct AsyncSlimTaskMethodBuilder<TResult>
    {
        /// <summary>The <see cref="AsyncTaskMethodBuilder{TResult}"/> to which most operations are delegated.</summary>
        private SlimTaskState<TResult> _state;
        private Action _runStateMachine;
        /// <summary>The result for this builder, if it's completed before any awaits occur.</summary>
        private TResult _result;

        public static AsyncSlimTaskMethodBuilder<TResult> Create()
        {
//            Console.WriteLine("AsyncSlimTaskMethodBuilder.Create called");
            return new AsyncSlimTaskMethodBuilder<TResult>();
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
//            Console.WriteLine("AsyncSlimTaskMethodBuilder.Start called");
            stateMachine.MoveNext();
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
//            Console.WriteLine($"AsyncSlimTaskMethodBuilder.SetStateMachine called, _state == null ? {_state == null}");
        }

        public void SetResult(TResult result)
        {
//            Console.WriteLine($"AsyncSlimTaskMethodBuilder.SetResult called, _state == null: {_state == null}");

            if (_state != null)
            {
                _state.SetResult(result);
            }
            else
            {
                _result = result;
            }
        }

        /// <summary>Marks the task as failed and binds the specified exception to the task.</summary>
        /// <param name="exception">The exception to bind to the task.</param>
        public void SetException(Exception exception)
        {
            Debug.Assert(_state != null);
            _state.SetException(exception);
        }

        /// <summary>Gets the task for this builder.</summary>
        public SlimTask<TResult> Task
        {
            get
            {
//                Console.WriteLine($"AsyncSlimTaskMethodBuilder.get_Task called, _state == null: {_state == null}");

                if (_state != null)
                {
                    return new SlimTask<TResult>(_state);
                }
                else
                {
                    return new SlimTask<TResult>(_result);
                }
            }
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">the awaiter</param>
        /// <param name="stateMachine">The state machine.</param>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            throw new NotImplementedException();
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">the awaiter</param>
        /// <param name="stateMachine">The state machine.</param>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
//            Console.WriteLine($"AsyncSlimTaskMethodBuilder.AwaitUnsafeOnCompleted called, _state == null: {_state == null}");

            // This is called whenever the async method needs to register an OnCompleted continuation with the specified awaiter.
            // Thus, if the state is not created yet, do it now.
            if (_state == null)
            {
//                Console.WriteLine("Instantiating state");

                // This structure lives on stateMachine, so we need to modify it before we copy stateMachine.
                var state = new SlimTaskStateWithStateMachine<TResult, TStateMachine>();
                _state = state;
                _runStateMachine = state.MoveNext;

                state.CopyStateMachine(ref stateMachine);
            }

            Debug.Assert(_runStateMachine != null);
            awaiter.UnsafeOnCompleted(_runStateMachine);
        }
    }

    /// <summary>Represents a builder for asynchronous methods that returns a <see cref="SlimTask{TResult}"/>.</summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    [StructLayout(LayoutKind.Auto)]
    public struct AsyncSlimTaskMethodBuilder
    {
        private SlimTaskState<VoidTaskResult> _state;
        private Action _runStateMachine;

        public static AsyncSlimTaskMethodBuilder Create()
        {
            return new AsyncSlimTaskMethodBuilder();
        }

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            stateMachine.MoveNext();
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine)
        {
        }

        public void SetResult()
        {
            if (_state != null)
            {
                _state.SetResult(new VoidTaskResult());
            }
        }

        /// <summary>Marks the task as failed and binds the specified exception to the task.</summary>
        /// <param name="exception">The exception to bind to the task.</param>
        public void SetException(Exception exception)
        {
            Debug.Assert(_state != null);
            _state.SetException(exception);
        }

        /// <summary>Gets the task for this builder.</summary>
        public SlimTask Task
        {
            get
            {
                if (_state != null)
                {
                    return new SlimTask(_state);
                }
                else
                {
                    return new SlimTask();
                }
            }
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">the awaiter</param>
        /// <param name="stateMachine">The state machine.</param>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            throw new NotImplementedException();
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        /// <typeparam name="TAwaiter">The type of the awaiter.</typeparam>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="awaiter">the awaiter</param>
        /// <param name="stateMachine">The state machine.</param>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            // This is called whenever the async method needs to register an OnCompleted continuation with the specified awaiter.
            // Thus, if the state is not created yet, do it now.
            if (_state == null)
            {
//                Console.WriteLine("Instantiating state");

                // This structure lives on stateMachine, so we need to modify it before we copy stateMachine.
                var state = new SlimTaskStateWithStateMachine<VoidTaskResult, TStateMachine>();
                _state = state;
                _runStateMachine = state.MoveNext;

                state.CopyStateMachine(ref stateMachine);
            }

            Debug.Assert(_runStateMachine != null);
            awaiter.UnsafeOnCompleted(_runStateMachine);
        }
    }
}
