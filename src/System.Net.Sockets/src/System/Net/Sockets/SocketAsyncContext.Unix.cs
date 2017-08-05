// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Enable this to turn on async queue tracing
#define TRACE

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets
{
    // Note on asynchronous behavior here:
    // TODO: Revise comment

    // The asynchronous socket operations here generally do the following:
    // (1) If the operation queue is empty, try to perform the operation immediately, non-blocking.
    // If this completes (i.e. does not return EWOULDBLOCK), then we return the results immediately
    // for both success (SocketError.Success) or failure.
    // No callback will happen; callers are expected to handle these synchronous completions themselves.
    // (2) If EWOULDBLOCK is returned, or the queue is not empty, then we enqueue an operation to the 
    // appropriate queue and return SocketError.IOPending.
    // Enqueuing itself may fail because the socket is closed before the operation can be enqueued;
    // in this case, we return SocketError.OperationAborted (which matches what Winsock would return in this case).
    // (3) When the queue completes the operation, it will post a work item to the threadpool
    // to call the callback with results (either success or failure).

    // Synchronous operations generally do the same, except that instead of returning IOPending,
    // they block on an event handle until the operation is processed by the queue.
    // Also, synchronous methods return SocketError.Interrupted when enqueuing fails
    // (which again matches Winsock behavior).

    internal sealed class SocketAsyncContext
    {
        private abstract class AsyncOperation
        {
            private enum State
            {
                Waiting = 0,
                Running = 1,
                Complete = 2,
                Cancelled = 3
            }

            private int _state; // Actually AsyncOperation.State.

#if DEBUG
            private int _callbackQueued; // When non-zero, the callback has been queued.
#endif

            public AsyncOperation Next;
            protected object CallbackOrEvent;
            public SocketError ErrorCode;
            public byte[] SocketAddress;
            public int SocketAddressLen;

            public ManualResetEventSlim Event
            {
                private get { return (ManualResetEventSlim)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            public AsyncOperation()
            {
                _state = (int)State.Waiting;
                Next = this;
            }

            public bool TryComplete(SocketAsyncContext context)
            {
                if (TraceEnabled) TraceWithContext(context, "Enter");

                bool result = DoTryComplete(context);

                if (TraceEnabled) TraceWithContext(context, $"Exit, result={result}");

                return result;
            }

            public bool TrySetRunning()
            {
                State oldState = (State)Interlocked.CompareExchange(ref _state, (int)State.Running, (int)State.Waiting);
                if (oldState == State.Cancelled)
                {
                    // This operation has already been cancelled, and had its completion processed.
                    // Simply return true to indicate no further processing is needed.
                    return false;
                }

                Debug.Assert(oldState == (int)State.Waiting);
                return true;
            }

            public void SetComplete()
            {
                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                Volatile.Write(ref _state, (int)State.Complete);
            }

            public void SetWaiting()
            {
                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                Volatile.Write(ref _state, (int)State.Waiting);
            }

            // This will go away, or at least change to something else
            public void ProcessCompletion()
            {
                var @event = CallbackOrEvent as ManualResetEventSlim;
                if (@event != null)
                {
                    @event.Set();
                }
                else
                {
#if DEBUG
                    Debug.Assert(Interlocked.CompareExchange(ref _callbackQueued, 1, 0) == 0, $"Unexpected _callbackQueued: {_callbackQueued}");
#endif

                    ThreadPool.QueueUserWorkItem(o => ((AsyncOperation)o).InvokeCallback(), this);
                }
            }

#if false
            public bool TryCompleteAsync(SocketAsyncContext context)
            {
                if (TraceEnabled) TraceWithContext(context, "Enter");

                Debug.Assert(context != null);

                State oldState = (State)Interlocked.CompareExchange(ref _state, (int)State.Running, (int)State.Waiting);
                if (oldState == State.Cancelled)
                {
                    // This operation has already been cancelled, and had its completion processed.
                    // Simply return true to indicate no further processing is needed.
                    if (TraceEnabled) TraceWithContext(context, "Exit, previously cancelled");
                    return true;
                }

                Debug.Assert(oldState == (int)State.Waiting);

                bool completed = DoTryComplete(context);

                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                if (!completed)
                {
                    // EAGAIN
                    Volatile.Write(ref _state, (int)State.Waiting);
                    if (TraceEnabled) TraceWithContext(context, "Exit, received EAGAIN");
                    return false;
                }

                // We've successfully completed this operation.  
                // Set state and process completion.

                Volatile.Write(ref _state, (int)State.Complete);

                if (TraceEnabled) TraceWithContext(context, $"I/O completed with {ErrorCode}, processing completion");

                var @event = CallbackOrEvent as ManualResetEventSlim;
                if (@event != null)
                {
                    @event.Set();
                }
                else
                {
#if DEBUG
                    Debug.Assert(Interlocked.CompareExchange(ref _callbackQueued, 1, 0) == 0, $"Unexpected _callbackQueued: {_callbackQueued}");
#endif

                    ThreadPool.QueueUserWorkItem(o => ((AsyncOperation)o).InvokeCallback(), this);
                }

                if (TraceEnabled) TraceWithContext(context, "Exit");

                return true;
            }
#endif

            public bool TryCancel()
            {
                if (TraceEnabled) Trace("Enter");

                // Try to transition from Waiting to Cancelled
                var spinWait = new SpinWait();
                bool keepWaiting = true;
                while (keepWaiting)
                {
                    int state = Interlocked.CompareExchange(ref _state, (int)State.Cancelled, (int)State.Waiting);
                    switch ((State)state)
                    {
                        case State.Running:
                            // A completion attempt is in progress. Keep busy-waiting.
                            if (TraceEnabled) Trace("Busy wait");
                            spinWait.SpinOnce();
                            break;

                        case State.Complete:
                            // A completion attempt succeeded. Consider this operation as having completed within the timeout.
                            if (TraceEnabled) Trace("Exit, previously completed");
                            return false;

                        case State.Waiting:
                            // This operation was successfully cancelled.
                            // Break out of the loop to handle the cancellation
                            keepWaiting = false;
                            break;

                        case State.Cancelled:
                            // Someone else cancelled the operation.
                            // Just return true to indicate the operation was cancelled.
                            // The previous canceller will have fired the completion, etc.
                            if (TraceEnabled) Trace("Exit, previously cancelled");
                            return true;
                    }
                }

                if (TraceEnabled) Trace("Cancelled, processing completion");

                // The operation successfully cancelled.  
                // It's our responsibility to set the error code and queue the completion.
                DoAbort();

                var @event = CallbackOrEvent as ManualResetEventSlim;
                if (@event != null)
                {
                    @event.Set();
                }
                else
                {
#if DEBUG
                    Debug.Assert(Interlocked.CompareExchange(ref _callbackQueued, 1, 0) == 0, $"Unexpected _callbackQueued: {_callbackQueued}");
#endif

                    ThreadPool.QueueUserWorkItem(o => ((AsyncOperation)o).InvokeCallback(), this);
                }

                if (TraceEnabled) Trace("Exit");

                // Note, we leave the operation in the OperationQueue.
                // When we get around to processing it, we'll see it's cancelled and skip it.
                return true;
            }

            // Called when op is not in the queue yet, so can't be otherwise executing
            public void DoAbort()
            {
                Abort();
                ErrorCode = SocketError.OperationAborted;
            }

            protected abstract void Abort();

            protected abstract bool DoTryComplete(SocketAsyncContext context);

            protected abstract void InvokeCallback();

            public void Trace(string message, [CallerMemberName] string memberName = null)
            {
                OutputTrace($"{IdOf(this)}.{memberName}: {message}");
            }

            public void TraceWithContext(SocketAsyncContext context, string message, [CallerMemberName] string memberName = null)
            {
                OutputTrace($"{IdOf(context)}, {IdOf(this)}.{memberName}: {message}");
            }
        }

        // These two abstract classes differentiate the operations that go in the
        // read queue vs the ones that go in the write queue.
        private abstract class ReadOperation : AsyncOperation 
        {
        }

        private abstract class WriteOperation : AsyncOperation 
        {
        }        

        private sealed class SendOperation : WriteOperation
        {
            public byte[] Buffer;
            public int Offset;
            public int Count;
            public SocketFlags Flags;
            public int BytesTransferred;
            public IList<ArraySegment<byte>> Buffers;
            public int BufferIndex;

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, SocketError> Callback
            {
                private get { return (Action<int, byte[], int, SocketFlags, SocketError>)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            protected sealed override void InvokeCallback()
            {
                Callback(BytesTransferred, SocketAddress, SocketAddressLen, SocketFlags.None, ErrorCode);
            }
            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                return SocketPal.TryCompleteSendTo(context._socket, Buffer, Buffers, ref BufferIndex, ref Offset, ref Count, Flags, SocketAddress, SocketAddressLen, ref BytesTransferred, out ErrorCode);
            }
        }

        private sealed class ReceiveOperation : ReadOperation
        {
            public byte[] Buffer;
            public int Offset;
            public int Count;
            public SocketFlags Flags;
            public int BytesTransferred;
            public SocketFlags ReceivedFlags;
            public IList<ArraySegment<byte>> Buffers;

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, SocketError> Callback
            {
                private get { return (Action<int, byte[], int, SocketFlags, SocketError>)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            protected sealed override void InvokeCallback()
            {
                Callback(BytesTransferred, SocketAddress, SocketAddressLen, ReceivedFlags, ErrorCode);
            }
            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                return SocketPal.TryCompleteReceiveFrom(context._socket, Buffer, Buffers, Offset, Count, Flags, SocketAddress, ref SocketAddressLen, out BytesTransferred, out ReceivedFlags, out ErrorCode);
            }
        }

        private sealed class ReceiveMessageFromOperation : ReadOperation
        {
            public byte[] Buffer;
            public int Offset;
            public int Count;
            public SocketFlags Flags;
            public int BytesTransferred;
            public SocketFlags ReceivedFlags;
            public IList<ArraySegment<byte>> Buffers;
            
            public bool IsIPv4;
            public bool IsIPv6;
            public IPPacketInformation IPPacketInformation;

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError> Callback
            {
                private get { return (Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError>)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                return SocketPal.TryCompleteReceiveMessageFrom(context._socket, Buffer, Buffers, Offset, Count, Flags, SocketAddress, ref SocketAddressLen, IsIPv4, IsIPv6, out BytesTransferred, out ReceivedFlags, out IPPacketInformation, out ErrorCode);
            }

            protected override void InvokeCallback()
            {
                Callback(BytesTransferred, SocketAddress, SocketAddressLen, ReceivedFlags, IPPacketInformation, ErrorCode);
            }
        }

        private sealed class AcceptOperation : ReadOperation
        {
            public IntPtr AcceptedFileDescriptor;

            public Action<IntPtr, byte[], int, SocketError> Callback
            {
                private get { return (Action<IntPtr, byte[], int, SocketError>)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            protected override void Abort()
            {
                AcceptedFileDescriptor = (IntPtr)(-1);
            }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool completed = SocketPal.TryCompleteAccept(context._socket, SocketAddress, ref SocketAddressLen, out AcceptedFileDescriptor, out ErrorCode);
                Debug.Assert(ErrorCode == SocketError.Success || AcceptedFileDescriptor == (IntPtr)(-1), $"Unexpected values: ErrorCode={ErrorCode}, AcceptedFileDescriptor={AcceptedFileDescriptor}");
                return completed;
            }

            protected override void InvokeCallback()
            {
                Callback(AcceptedFileDescriptor, SocketAddress, SocketAddressLen, ErrorCode);
            }
        }

        private sealed class ConnectOperation : WriteOperation
        {
            public Action<SocketError> Callback
            {
                private get { return (Action<SocketError>)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            protected override void Abort() { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool result = SocketPal.TryCompleteConnect(context._socket, SocketAddressLen, out ErrorCode);
                context._socket.RegisterConnectResult(ErrorCode);
                return result;
            }

            protected override void InvokeCallback()
            {
                Callback(ErrorCode);
            }
        }

        private sealed class SendFileOperation : WriteOperation
        {
            public SafeFileHandle FileHandle;
            public long Offset;
            public long Count;
            public long BytesTransferred;

            protected override void Abort() { }

            public Action<long, SocketError> Callback
            {
                private get { return (Action<long, SocketError>)CallbackOrEvent; }
                set { CallbackOrEvent = value; }
            }

            protected override void InvokeCallback()
            {
                Callback(BytesTransferred, ErrorCode);
            }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                return SocketPal.TryCompleteSendFile(context._socket, FileHandle, ref Offset, ref Count, ref BytesTransferred, out ErrorCode);
            }
        }

        // In debug builds, this struct guards against:
        // (1) Unexpected lock reentrancy, which should never happen
        // (2) Deadlock, by setting a reasonably large timeout
        private struct LockToken : IDisposable
        {
            private object _lockObject;

            public LockToken(object lockObject)
            {
                Debug.Assert(lockObject != null);

                _lockObject = lockObject;

                Debug.Assert(!Monitor.IsEntered(_lockObject));

#if DEBUG
                bool success = Monitor.TryEnter(_lockObject, 10000);
                Debug.Assert(success);
#else
                Monitor.Enter(_lockObject);
#endif
            }

            public void Dispose()
            {
                Debug.Assert(Monitor.IsEntered(_lockObject));
                Monitor.Exit(_lockObject);
            }
        }

        private struct OperationQueue<TOperation>
            where TOperation : AsyncOperation
        {
            // Description of queue states:
            // Ready means that there MAY be data available from the OS.
            // It also means the queue is empty.
            // Callers are expected to check IsReady and, if true, try to perform the operation directly.
            // If this fails (or IsReady was false), then callers should call StartAsyncOperation,
            // which will transition us to Waiting (if we weren't already) and enqueue the operation.
            // StartAsyncOperation may also complete synchronously (when?)

            private enum QueueState
            {
                Ready = 0,
                Waiting = 1,
                Processing = 2,
                Stopped = 3,
            }

            private object _queueLock;
            private AsyncOperation _tail;
            private QueueState _state;
            private int _sequenceNumber;

            private LockToken Lock() => new LockToken(_queueLock);

            private static WaitCallback s_processingCallback =
                typeof(TOperation) == typeof(ReadOperation) ? ((o) => { var context = ((SocketAsyncContext)o); context._receiveQueue.ProcessQueue(context); }) :
                typeof(TOperation) == typeof(WriteOperation) ? ((o) => { var context = ((SocketAsyncContext)o); context._sendQueue.ProcessQueue(context); }) :
                (WaitCallback)null;

            public void Init()
            {
                Debug.Assert(_queueLock == null);
                _queueLock = new object();

                _state = QueueState.Ready;
                _sequenceNumber = 0;
            }

            public bool IsReady(SocketAsyncContext context, out int observedSequenceNumber)
            {
                using (Lock())
                {
                    observedSequenceNumber = _sequenceNumber;
                    bool isReady = (_state == QueueState.Ready);

                    if (TraceEnabled) Trace(context, $"{isReady}");

                    return isReady;
                }
            }

                //  Maybe rename for PerformAsyncOperation, like above
                // Return true for pending, false for completed sync (incl failure and abort)
            public bool StartAsyncOperation(SocketAsyncContext context, TOperation operation, int observedSequenceNumber)
            {
                if (TraceEnabled) Trace(context, $"Enter");

                if (!context._registered)
                {
                    context.Register();
                }

                while (true)
                {
                    bool doAbort = false;
                    using (Lock())
                    {
                        switch (_state)
                        {
                            case QueueState.Ready:
                                if (observedSequenceNumber != _sequenceNumber)
                                {
                                    // The queue has become ready again since we previously checked it.
                                    // So, we need to retry the operation before we enqueue it.
                                    Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                                    observedSequenceNumber = _sequenceNumber;
                                    break;
                                }

                                // Caller tried the operation and got an EAGAIN, so we need to transition.
                                _state = QueueState.Waiting;
                                goto case QueueState.Waiting;

                            case QueueState.Waiting:
                            case QueueState.Processing:
                                // Enqueue the operation.
                                Debug.Assert(operation.Next == operation, "Expected operation.Next == operation");

                                if (_tail != null)
                                {
                                    operation.Next = _tail.Next;
                                    _tail.Next = operation;
                                }

                                _tail = operation;

                                if (TraceEnabled) Trace(context, $"Leave, enqueued {IdOf(operation)}");
                                return true;

                            case QueueState.Stopped:
                                Debug.Assert(_tail == null);
                                doAbort = true;
                                break;

                            default:
                                Environment.FailFast("unexpected queue state");
                                break;
                        }
                    }

                    if (doAbort)
                    {
                        operation.DoAbort();
                        if (TraceEnabled) Trace(context, $"Leave, queue stopped");
                        return false;
                    }

                    // Retry the operation.
                    if (operation.TryComplete(context))
                    {
                        if (TraceEnabled) Trace(context, $"Leave, retry succeeded");
                        return false;
                    }
                }
            }

            public void HandleEvent(SocketAsyncContext context)
            {
                using (Lock())
                {
                    if (TraceEnabled) Trace(context, $"Enter");

                    switch (_state)
                    {
                        case QueueState.Ready:
                            Debug.Assert(_tail == null, "State == Ready but queue is not empty!");
                            _sequenceNumber++;
                            if (TraceEnabled) Trace(context, $"Exit (previously ready)");
                            return;

                        case QueueState.Waiting:
                            Debug.Assert(_tail != null, "State == Waiting but queue is empty!");
                            _state = QueueState.Processing;
                            // Break out and release lock
                            break;

                        case QueueState.Processing:
                            Debug.Assert(_tail != null, "State == Processing but queue is empty!");
                            _sequenceNumber++;
                            if (TraceEnabled) Trace(context, $"Exit (currently processing)");
                            return;

                        case QueueState.Stopped:
                            Debug.Assert(_tail == null);
                            if (TraceEnabled) Trace(context, $"Exit (stopped)");
                            return;

                        default:
                            Environment.FailFast("unexpected queue state");
                            return;
                    }
                }

                // We just transitioned from Waiting to Processing.
                // Spawn a work item to do the actual processing.
                ThreadPool.QueueUserWorkItem(s_processingCallback, context);
            }

            // CONSIDER:  If I'm tracing outside of locks, I may get inconsistent data...
            public void ProcessQueue(SocketAsyncContext context)
            {
                int observedSequenceNumber;
                AsyncOperation op;
                using (Lock())
                {
                    if (TraceEnabled) Trace(context, $"Enter");

                    switch (_state)
                    {
                        case QueueState.Ready:
                        case QueueState.Waiting:
                            Debug.Assert(false, $"Unexpected queue state while processing I/O: {_state}");
                            Debug.Assert(_tail == null, "State == Ready but queue is not empty!");
                            return;

                        case QueueState.Processing:
                            Debug.Assert(_tail != null, "Unexpected empty queue while processing I/O");
                            observedSequenceNumber = _sequenceNumber;
                            op = _tail.Next;        // head of queue
                            break;

                        case QueueState.Stopped:
                            Debug.Assert(_tail == null);
                            if (TraceEnabled) Trace(context, $"Exit (stopped)");
                            return;

                        default:
                            Environment.FailFast("unexpected queue state");
                            return;
                    }
                }

                while (true)
                {
                    bool wasCompleted = false;
                    bool wasCancelled = !op.TrySetRunning();
                    if (!wasCancelled)
                    {
                        wasCompleted = op.TryComplete(context);
                        if (wasCompleted)
                        {
                            op.SetComplete();
                            op.ProcessCompletion();
                        }
                        else
                        {
                            op.SetWaiting();
                        }
                    }

                    if (wasCompleted || wasCancelled)
                    {
                        // Remove the op from the queue and see if there's more to process.

                        using (Lock())
                        {
                            if (_state == QueueState.Stopped)
                            {
                                Debug.Assert(_tail == null);
                                if (TraceEnabled) Trace(context, $"Exit (stopped)");
                                return;
                            }

                            Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");

                            if (op == _tail)
                            {
                                // No more operations to process
                                _tail = null;
                                _state = QueueState.Ready;
                                _sequenceNumber++;
                                if (TraceEnabled) Trace(context, $"Exit (finished queue)");
                                return;
                            }
                            else
                            {
                                // Pop current operation and advance to next
                                op = _tail.Next = op.Next;
                            }
                        }
                    }
                    else
                    {
                        // Check for retry and reset queue state.

                        using (Lock())
                        {
                            if (_state == QueueState.Stopped)
                            {
                                Debug.Assert(_tail == null);
                                if (TraceEnabled) Trace(context, $"Exit (stopped)");
                                return;
                            }

                            Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");

                            if (observedSequenceNumber != _sequenceNumber)
                            {
                                // We received another epoll notification since we previously checked it.
                                // So, we need to retry the operation.
                                Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                                observedSequenceNumber = _sequenceNumber;
                            }
                            else
                            {
                                _state = QueueState.Waiting;
                                if (TraceEnabled) Trace(context, $"Exit (received EAGAIN)");
                                return;
                            }
                        }
                    }
                }
            }

            public void StopAndAbort(SocketAsyncContext context)
            {
                // We should be called exactly once, by SafeCloseSocket.
                Debug.Assert(_state != QueueState.Stopped);

                using (Lock())
                {
                    if (TraceEnabled) Trace(context, $"Enter");

                    Debug.Assert(_state != QueueState.Stopped);

                    _state = QueueState.Stopped;

                    if (_tail != null)
                    {
                        AsyncOperation op = _tail;
                        do
                        {
                            op.TryCancel();
                            op = op.Next;
                        } while (op != _tail);
                    }

                    _tail = null;

                    if (TraceEnabled) Trace(context, $"Exit");
                }
            }

            public void Trace(SocketAsyncContext context, string message, [CallerMemberName] string memberName = null)
            {
                string queueType =
                    typeof(TOperation) == typeof(ReadOperation) ? "recv" :
                    typeof(TOperation) == typeof(WriteOperation) ? "send" :
                    "???";

                OutputTrace($"{IdOf(context)}-{queueType}.{memberName}: {message}, {_state}-{_sequenceNumber}, {((_tail == null) ? "empty" : "not empty")}");
            }
        }

        private readonly SafeCloseSocket _socket;
        private OperationQueue<ReadOperation> _receiveQueue;
        private OperationQueue<WriteOperation> _sendQueue;
        private SocketAsyncEngine.Token _asyncEngineToken;
        private bool _registered;
        private bool _nonBlockingSet;

        private readonly object _registerLock = new object();

        public SocketAsyncContext(SafeCloseSocket socket)
        {
            _socket = socket;

            _receiveQueue.Init();
            _sendQueue.Init();
        }

        private void Register()
        {
            Debug.Assert(_nonBlockingSet);
            lock (_registerLock)
            {
                if (!_registered)
                {
                    Debug.Assert(!_asyncEngineToken.WasAllocated);
                    var token = new SocketAsyncEngine.Token(this);

                    Interop.Error errorCode;
                    if (!token.TryRegister(_socket, out errorCode))
                    {
                        token.Free();
                        if (errorCode == Interop.Error.ENOMEM || errorCode == Interop.Error.ENOSPC)
                        {
                            throw new OutOfMemoryException();
                        }
                        else
                        {
                            throw new InternalException();
                        }
                    }

                    _asyncEngineToken = token;
                    _registered = true;

                    if (TraceEnabled) Trace("Registered");
                }
            }
        }

        public void Close()
        {
            // Drain queues
            _sendQueue.StopAndAbort(this);
            _receiveQueue.StopAndAbort(this);

            lock (_registerLock)
            { 
                // Freeing the token will prevent any future event delivery.  This socket will be unregistered
                // from the event port automatically by the OS when it's closed.
                _asyncEngineToken.Free();
            }
        }

        public void SetNonBlocking()
        {
            //
            // Our sockets may start as blocking, and later transition to non-blocking, either because the user
            // explicitly requested non-blocking mode, or because we need non-blocking mode to support async
            // operations.  We never transition back to blocking mode, to avoid problems synchronizing that
            // transition with the async infrastructure.
            //
            // Note that there's no synchronization here, so we may set the non-blocking option multiple times
            // in a race.  This should be fine.
            //
            if (!_nonBlockingSet)
            {
                if (Interop.Sys.Fcntl.SetIsNonBlocking(_socket, 1) != 0)
                {
                    throw new SocketException((int)SocketPal.GetSocketErrorForErrorCode(Interop.Sys.GetLastError()));
                }

                _nonBlockingSet = true;
            }
        }

        private void PerformSyncOperation<TOperation>(ref OperationQueue<TOperation> queue, TOperation operation, int timeout, int observedSequenceNumber)
            where TOperation : AsyncOperation
        {
            using (var e = new ManualResetEventSlim(false, 0))
            {
                operation.Event = e;

                if (!queue.StartAsyncOperation(this, operation, observedSequenceNumber))
                {
                    // Completed synchronously
                    return;
                }

                if (e.Wait(timeout))
                {
                    // Completed within timeout
                    return;
                }

                bool cancelled = operation.TryCancel();
                if (cancelled)
                {
                    operation.ErrorCode = SocketError.TimedOut;
                }
            }
        }

        public SocketError Accept(byte[] socketAddress, ref int socketAddressLen, int timeout, out IntPtr acceptedFd)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
            {
                Debug.Assert(errorCode == SocketError.Success || acceptedFd == (IntPtr)(-1), $"Unexpected values: errorCode={errorCode}, acceptedFd={acceptedFd}");
                return errorCode;
            }

            var operation = new AcceptOperation
            {
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            PerformSyncOperation(ref _receiveQueue, operation, timeout, observedSequenceNumber);

            socketAddressLen = operation.SocketAddressLen;
            acceptedFd = operation.AcceptedFileDescriptor;
            return operation.ErrorCode;
        }

        public SocketError AcceptAsync(byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd, Action<IntPtr, byte[], int, SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
            {
                Debug.Assert(errorCode == SocketError.Success || acceptedFd == (IntPtr)(-1), $"Unexpected values: errorCode={errorCode}, acceptedFd={acceptedFd}");

                return errorCode;
            }

            var operation = new AcceptOperation {
                Callback = callback,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                socketAddressLen = operation.SocketAddressLen;
                acceptedFd = operation.AcceptedFileDescriptor;
                return operation.ErrorCode;
            }

            acceptedFd = (IntPtr)(-1);
            return SocketError.IOPending;
        }

        public SocketError Connect(byte[] socketAddress, int socketAddressLen, int timeout)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            // Connect is different than the usual "readiness" pattern of other operations.
            // We need to initiate the connect before we try to complete it. 
            // Thus, always call TryStartConnect regardless of readiness.
            SocketError errorCode;
            int observedSequenceNumber;
            _sendQueue.IsReady(this, out observedSequenceNumber);
            if (SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                _socket.RegisterConnectResult(errorCode);
                return errorCode;
            }

            var operation = new ConnectOperation
            {
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            PerformSyncOperation(ref _sendQueue, operation, timeout, observedSequenceNumber);

            return operation.ErrorCode;
        }

        public SocketError ConnectAsync(byte[] socketAddress, int socketAddressLen, Action<SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            SetNonBlocking();

            // Connect is different than the usual "readiness" pattern of other operations.
            // We need to initiate the connect before we try to complete it. 
            // Thus, always call TryStartConnect regardless of readiness.
            SocketError errorCode;
            int observedSequenceNumber;
            _sendQueue.IsReady(this, out observedSequenceNumber);
            if (SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                _socket.RegisterConnectResult(errorCode);
                return errorCode;
            }

            var operation = new ConnectOperation {
                Callback = callback,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                return operation.ErrorCode;
            }

            return SocketError.IOPending;
        }

        public SocketError Receive(byte[] buffer, int offset, int count, ref SocketFlags flags, int timeout, out int bytesReceived)
        {
            int socketAddressLen = 0;
            return ReceiveFrom(buffer, offset, count, ref flags, null, ref socketAddressLen, timeout, out bytesReceived);
        }

        public SocketError ReceiveAsync(byte[] buffer, int offset, int count, SocketFlags flags, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return ReceiveFromAsync(buffer, offset, count, flags, null, ref socketAddressLen, out bytesReceived, out receivedFlags, callback);
        }

        public SocketError ReceiveFrom(byte[] buffer, int offset, int count, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, int timeout, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketFlags receivedFlags;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                flags = receivedFlags;
                return errorCode;
            }

            var operation = new ReceiveOperation
            {
                Buffer = buffer,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
            };

            PerformSyncOperation(ref _receiveQueue, operation, timeout, observedSequenceNumber);

            flags = operation.ReceivedFlags;
            bytesReceived = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError ReceiveFromAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                return errorCode;
            }

            var operation = new ReceiveOperation
            {
                Callback = callback,
                Buffer = buffer,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
            };

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                receivedFlags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            bytesReceived = 0;
            receivedFlags = SocketFlags.None;
            return SocketError.IOPending;
        }

        public SocketError Receive(IList<ArraySegment<byte>> buffers, ref SocketFlags flags, int timeout, out int bytesReceived)
        {
            return ReceiveFrom(buffers, ref flags, null, 0, timeout, out bytesReceived);
        }

        public SocketError ReceiveAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return ReceiveFromAsync(buffers, flags, null, ref socketAddressLen, out bytesReceived, out receivedFlags, callback);
        }

        public SocketError ReceiveFrom(IList<ArraySegment<byte>> buffers, ref SocketFlags flags, byte[] socketAddress, int socketAddressLen, int timeout, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketFlags receivedFlags;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                flags = receivedFlags;
                return errorCode;
            }

            var operation = new ReceiveOperation
            {
                Buffers = buffers,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
            };

            PerformSyncOperation(ref _receiveQueue, operation, timeout, observedSequenceNumber);

            socketAddressLen = operation.SocketAddressLen;
            flags = operation.ReceivedFlags;
            bytesReceived = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError ReceiveFromAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                // Synchronous success or failure
                return errorCode;
            }

            var operation = new ReceiveOperation
            {
                Callback = callback,
                Buffers = buffers,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
            };

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                socketAddressLen = operation.SocketAddressLen;
                receivedFlags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            receivedFlags = SocketFlags.None;
            bytesReceived = 0;
            return SocketError.IOPending;
        }

        public SocketError ReceiveMessageFrom(
            byte[] buffer, IList<ArraySegment<byte>> buffers, int offset, int count, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, int timeout, out IPPacketInformation ipPacketInformation, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketFlags receivedFlags;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer, buffers, offset, count, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
            {
                flags = receivedFlags;
                return errorCode;
            }

            var operation = new ReceiveMessageFromOperation
            {
                Buffer = buffer,
                Buffers = buffers,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                IsIPv4 = isIPv4,
                IsIPv6 = isIPv6,
            };

            PerformSyncOperation(ref _receiveQueue, operation, timeout, observedSequenceNumber);

            socketAddressLen = operation.SocketAddressLen;
            flags = operation.ReceivedFlags;
            ipPacketInformation = operation.IPPacketInformation;
            bytesReceived = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError ReceiveMessageFromAsync(byte[] buffer, IList<ArraySegment<byte>> buffers, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, out int bytesReceived, out SocketFlags receivedFlags, out IPPacketInformation ipPacketInformation, Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (_receiveQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer, buffers, offset, count, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
            {
                return errorCode;
            }

            var operation = new ReceiveMessageFromOperation
            {
                Callback = callback,
                Buffer = buffer,
                Buffers = buffers,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                IsIPv4 = isIPv4,
                IsIPv6 = isIPv6,
            };

            if (!_receiveQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                socketAddressLen = operation.SocketAddressLen;
                receivedFlags = operation.ReceivedFlags;
                ipPacketInformation = operation.IPPacketInformation;
                bytesReceived = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            ipPacketInformation = default(IPPacketInformation);
            bytesReceived = 0;
            receivedFlags = SocketFlags.None;
            return SocketError.IOPending;
        }

        public SocketError Send(byte[] buffer, int offset, int count, SocketFlags flags, int timeout, out int bytesSent)
        {
            return SendTo(buffer, offset, count, flags, null, 0, timeout, out bytesSent);
        }

        public SocketError SendAsync(byte[] buffer, int offset, int count, SocketFlags flags, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return SendToAsync(buffer, offset, count, flags, null, ref socketAddressLen, out bytesSent, callback);
        }

        public SocketError SendTo(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, int socketAddressLen, int timeout, out int bytesSent)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffer, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendOperation
            {
                Buffer = buffer,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                BytesTransferred = bytesSent
            };

            PerformSyncOperation(ref _sendQueue, operation, timeout, observedSequenceNumber);

            bytesSent = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError SendToAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffer, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendOperation
            {
                Callback = callback,
                Buffer = buffer,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                BytesTransferred = bytesSent
            };

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }
                
            return SocketError.IOPending;
        }

        public SocketError Send(IList<ArraySegment<byte>> buffers, SocketFlags flags, int timeout, out int bytesSent)
        {
            return SendTo(buffers, flags, null, 0, timeout, out bytesSent);
        }

        public SocketError SendAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return SendToAsync(buffers, flags, null, ref socketAddressLen, out bytesSent, callback);
        }

        public SocketError SendTo(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, int socketAddressLen, int timeout, out int bytesSent)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            bytesSent = 0;
            int bufferIndex = 0;
            int offset = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendOperation
            {
                Buffers = buffers,
                BufferIndex = bufferIndex,
                Offset = offset,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                BytesTransferred = bytesSent
            };

            PerformSyncOperation(ref _sendQueue, operation, timeout, observedSequenceNumber);

            bytesSent = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError SendToAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            int bufferIndex = 0;
            int offset = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendOperation
            {
                Callback = callback,
                Buffers = buffers,
                BufferIndex = bufferIndex,
                Offset = offset,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                BytesTransferred = bytesSent
            };

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            return SocketError.IOPending;
        }

        public SocketError SendFile(SafeFileHandle fileHandle, long offset, long count, int timeout, out long bytesSent)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendFileOperation
            {
                FileHandle = fileHandle,
                Offset = offset,
                Count = count,
                BytesTransferred = bytesSent
            };

            PerformSyncOperation(ref _sendQueue, operation, timeout, observedSequenceNumber);

            bytesSent = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError SendFileAsync(SafeFileHandle fileHandle, long offset, long count, out long bytesSent, Action<long, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (_sendQueue.IsReady(this, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            var operation = new SendFileOperation
            {
                Callback = callback,
                FileHandle = fileHandle,
                Offset = offset,
                Count = count,
                BytesTransferred = bytesSent
            };

            if (!_sendQueue.StartAsyncOperation(this, operation, observedSequenceNumber))
            {
                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }

            return SocketError.IOPending;
        }

        public unsafe void HandleEvents(Interop.Sys.SocketEvents events)
        {
            if ((events & Interop.Sys.SocketEvents.ReadClose) != 0 &&
                (events & Interop.Sys.SocketEvents.Read) == 0)
            {
                Debug.Assert(false, "Saw ReadClose without Read");
            }

            if ((events & Interop.Sys.SocketEvents.Error) != 0)
            {
                // Set the Read and Write flags as well; the processing for these events
                // will pick up the error.
                events |= Interop.Sys.SocketEvents.Read | Interop.Sys.SocketEvents.Write;
            }

            if ((events & Interop.Sys.SocketEvents.Read) != 0)
            {
                _receiveQueue.HandleEvent(this);
            }

            if ((events & Interop.Sys.SocketEvents.Write) != 0)
            {
                _sendQueue.HandleEvent(this);
            }
        }

        //
        // Tracing stuff
        //

            // temporary; default to false
        public static bool TraceEnabled => true;

        public void Trace(string message, [CallerMemberName] string memberName = null)
        {
            OutputTrace($"{IdOf(this)}.{memberName}: {message}");
        }

        public static void OutputTrace(string s)
        {
            // CONSIDER: Change to NetEventSource
#if TRACE
            Console.WriteLine(s);
#endif
        }

        public static string IdOf(object o) => o == null ? "(null)" : $"{o.GetType().Name}#{o.GetHashCode():X2}";
    }
}
