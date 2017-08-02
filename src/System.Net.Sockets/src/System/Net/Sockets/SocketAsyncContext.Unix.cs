// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

// Enable this to turn on async queue tracing
#define TRACE

using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace System.Net.Sockets
{
    // Note on asynchronous behavior here:

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
                Debug.Assert(_state == (int)State.Waiting, $"Unexpected _state: {_state}");

                bool result = DoTryComplete(context);

#if TRACE
                Trace($"{IdOf(this)}: TryComplete, context={IdOf(context)}");
#endif

                return result;
            }

            public bool TryCompleteAsync(SocketAsyncContext context)
            {
                Debug.Assert(context != null);

                State oldState = (State)Interlocked.CompareExchange(ref _state, (int)State.Running, (int)State.Waiting);
                if (oldState == State.Cancelled)
                {
                    // This operation has already been cancelled, and had its completion processed.
                    // Simply return true to indicate no further processing is needed.
#if TRACE
                    Trace($"{IdOf(this)}: TryCompleteAsync: already cancelled, context={IdOf(context)}");
#endif

                    return true;
                }

                Debug.Assert(oldState == (int)State.Waiting);

                bool completed = DoTryComplete(context);

                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                if (!completed)
                {
                    // EAGAIN
                    Volatile.Write(ref _state, (int)State.Waiting);
#if TRACE
                    Trace($"{IdOf(this)}: TryCompleteAsync: failed to complete, context={IdOf(context)}");
#endif
                    return false;
                }

                // We've successfully completed this operation.  
                // Set state and process completion.

                Volatile.Write(ref _state, (int)State.Complete);

#if TRACE
                Trace($"{IdOf(this)}: TryCompleteAsync: processing completion, context={IdOf(context)}");
#endif

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

                return true;
            }

            public bool TryCancel()
            {
#if TRACE
                Trace($"{IdOf(this)}: Enter TryCancel");
#endif

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
#if TRACE
                            Trace($"{IdOf(this)}: TryCancel busy wait");
#endif
                            spinWait.SpinOnce();
                            break;

                        case State.Complete:
                            // A completion attempt succeeded. Consider this operation as having completed within the timeout.
#if TRACE
                            Trace($"{IdOf(this)}: TryCancel: already completed");
#endif
                            return false;

                        case State.Waiting:
                            // This operation was successfully cancelled.
                            // Break out of the loop to handle the cancellation
#if TRACE
                            Trace($"{IdOf(this)}: TryCancel: processing cancellation");
#endif
                            keepWaiting = false;
                            break;

                        case State.Cancelled:
                            // Someone else cancelled the operation.
                            // Just return true to indicate the operation was cancelled.
                            // The previous canceller will have fired the completion, etc.
#if TRACE
                            Trace($"{IdOf(this)}: TryCancel: already cancelled");
#endif
                            return true;
                    }
                }

                // The operation successfully cancelled.  
                // It's our responsibility to set the error code and invoke the completion.
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

                // Note, we leave the operation in the OperationQueue.
                // When we get around to processing it, we'll see it's cancelled and skip it.
                return true;
            }

            public bool Wait(int timeout)
            {
                if (Event.Wait(timeout))
                {
                    return true;
                }

#if TRACE
                Trace($"{IdOf(this)}: Wait: timed out");
#endif

                bool cancelled = TryCancel();

                if (cancelled)
                {
                    Debug.Assert(_state == (int)State.Cancelled);
                    return false;
                }

                Debug.Assert(_state == (int)State.Complete);
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
        }

        // These two abstract classes differentiate the operations that go in the
        // read queue vs the ones that go in the write queue.
        private abstract class ReadOperation : AsyncOperation 
        {
        }

        private abstract class WriteOperation : AsyncOperation 
        {
        }

        private abstract class SendOperation : WriteOperation
        {
            public SocketFlags Flags;
            public int BytesTransferred;
            public int Offset;
            public int Count;

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, SocketError> Callback
            {
                set => CallbackOrEvent = value;
            }

            protected sealed override void InvokeCallback() =>
                ((Action<int, byte[], int, SocketFlags, SocketError>)CallbackOrEvent)(BytesTransferred, SocketAddress, SocketAddressLen, SocketFlags.None, ErrorCode);
        }

        private sealed class BufferArraySendOperation : SendOperation
        {
            public byte[] Buffer;

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                int bufferIndex = 0;
                return SocketPal.TryCompleteSendTo(context._socket, Buffer, null, ref bufferIndex, ref Offset, ref Count, Flags, SocketAddress, SocketAddressLen, ref BytesTransferred, out ErrorCode);
            }
        }

        private sealed class BufferListSendOperation : SendOperation
        {
            public IList<ArraySegment<byte>> Buffers;
            public int BufferIndex;

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                return SocketPal.TryCompleteSendTo(context._socket, default(ReadOnlySpan<byte>), Buffers, ref BufferIndex, ref Offset, ref Count, Flags, SocketAddress, SocketAddressLen, ref BytesTransferred, out ErrorCode);
            }
        }

        private sealed unsafe class BufferPtrSendOperation : SendOperation
        {
            public byte* BufferPtr;

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                int bufferIndex = 0;
                return SocketPal.TryCompleteSendTo(context._socket, new ReadOnlySpan<byte>(BufferPtr, Offset + Count), null, ref bufferIndex, ref Offset, ref Count, Flags, SocketAddress, SocketAddressLen, ref BytesTransferred, out ErrorCode);
            }
        }

        private abstract class ReceiveOperation : ReadOperation
        {
            public SocketFlags Flags;
            public SocketFlags ReceivedFlags;
            public int BytesTransferred;

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, SocketError> Callback
            {
                set => CallbackOrEvent = value;
            }

            protected sealed override void InvokeCallback() =>
                ((Action<int, byte[], int, SocketFlags, SocketError>)CallbackOrEvent)(
                    BytesTransferred, SocketAddress, SocketAddressLen, ReceivedFlags, ErrorCode);
        }

        private sealed class BufferArrayReceiveOperation : ReceiveOperation
        {
            public byte[] Buffer;
            public int Offset;
            public int Count;

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteReceiveFrom(context._socket, new Span<byte>(Buffer, Offset, Count), null, Flags, SocketAddress, ref SocketAddressLen, out BytesTransferred, out ReceivedFlags, out ErrorCode);
        }

        private sealed class BufferListReceiveOperation : ReceiveOperation
        {
            public IList<ArraySegment<byte>> Buffers;

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteReceiveFrom(context._socket, default(Span<byte>), Buffers, Flags, SocketAddress, ref SocketAddressLen, out BytesTransferred, out ReceivedFlags, out ErrorCode);
        }

        private sealed unsafe class BufferPtrReceiveOperation : ReceiveOperation
        {
            public byte* BufferPtr;
            public int Length;

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteReceiveFrom(context._socket, new Span<byte>(BufferPtr, Length), null, Flags, SocketAddress, ref SocketAddressLen, out BytesTransferred, out ReceivedFlags, out ErrorCode);
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
                set => CallbackOrEvent = value;
            }

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteReceiveMessageFrom(context._socket, Buffer, Buffers, Offset, Count, Flags, SocketAddress, ref SocketAddressLen, IsIPv4, IsIPv6, out BytesTransferred, out ReceivedFlags, out IPPacketInformation, out ErrorCode);

            protected override void InvokeCallback() =>
                ((Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError>)CallbackOrEvent)(
                    BytesTransferred, SocketAddress, SocketAddressLen, ReceivedFlags, IPPacketInformation, ErrorCode);
        }

        private sealed class AcceptOperation : ReadOperation
        {
            public IntPtr AcceptedFileDescriptor;

            public Action<IntPtr, byte[], int, SocketError> Callback
            {
                set => CallbackOrEvent = value;
            }

            protected override void Abort() =>
                AcceptedFileDescriptor = (IntPtr)(-1);

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool completed = SocketPal.TryCompleteAccept(context._socket, SocketAddress, ref SocketAddressLen, out AcceptedFileDescriptor, out ErrorCode);
                Debug.Assert(ErrorCode == SocketError.Success || AcceptedFileDescriptor == (IntPtr)(-1), $"Unexpected values: ErrorCode={ErrorCode}, AcceptedFileDescriptor={AcceptedFileDescriptor}");
                return completed;
            }

            protected override void InvokeCallback() =>
                ((Action<IntPtr, byte[], int, SocketError>)CallbackOrEvent)(
                    AcceptedFileDescriptor, SocketAddress, SocketAddressLen, ErrorCode);
        }

        private sealed class ConnectOperation : WriteOperation
        {
            public Action<SocketError> Callback
            {
                set => CallbackOrEvent = value;
            }

            protected override void Abort() { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool result = SocketPal.TryCompleteConnect(context._socket, SocketAddressLen, out ErrorCode);
                context._socket.RegisterConnectResult(ErrorCode);
                return result;
            }

            protected override void InvokeCallback() =>
                ((Action<SocketError>)CallbackOrEvent)(ErrorCode);
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
                set => CallbackOrEvent = value;
            }

            protected override void InvokeCallback() =>
                ((Action<long, SocketError>)CallbackOrEvent)(BytesTransferred, ErrorCode);

            protected override bool DoTryComplete(SocketAsyncContext context) =>
                SocketPal.TryCompleteSendFile(context._socket, FileHandle, ref Offset, ref Count, ref BytesTransferred, out ErrorCode);
        }

        private enum QueueState
        {
            Clear = 0,
            Set = 1,
            Stopped = 2,
        }

        // This struct guards against:
        // (1) Unexpected lock reentrancy, which should never happen
        // (2) Deadlock, by setting a reasonably large timeout
        // TODO: Only do this in debug, likely
        private struct LockToken : IDisposable
        {
            private object _lockObject;

            public LockToken(object lockObject)
            {
                Debug.Assert(lockObject != null);

                _lockObject = lockObject;

                Debug.Assert(!Monitor.IsEntered(_lockObject));

                bool success = Monitor.TryEnter(_lockObject, 2000);
                Debug.Assert(success);
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
            private object _queueLock;
            private AsyncOperation _tail;

            public QueueState State { get; set; }
            public bool IsStopped { get { return State == QueueState.Stopped; } }
            public bool IsEmpty { get { return _tail == null; } }

            // These should not be public
            public LockToken Lock() => new LockToken(_queueLock);
            public bool IsLocked => Monitor.IsEntered(_queueLock);

#if false
            public object QueueLock
            {
                get
                {
                    // Make sure we don't have unexpected reentrancy
                    Debug.Assert(!Monitor.IsEntered(_queueLock));
                    return _queueLock;
                }
            }
#endif

            public void Init()
            {
                Debug.Assert(_queueLock == null);
                _queueLock = new object();
            }

            public void Enqueue(TOperation operation)
            {
                Debug.Assert(!IsStopped, "Expected !IsStopped");
                Debug.Assert(operation.Next == operation, "Expected operation.Next == operation");

                if (!IsEmpty)
                {
                    operation.Next = _tail.Next;
                    _tail.Next = operation;
                }

                _tail = operation;
            }

#if false
            private bool TryDequeue(out TOperation operation)
            {
                if (_tail == null)
                {
                    operation = null;
                    return false;
                }

                AsyncOperation head = _tail.Next;
                if (head == _tail)
                {
                    _tail = null;
                }
                else
                {
                    _tail.Next = head.Next;
                }

                head.Next = null;
                operation = (TOperation)head;
                return true;
            }

            private void Requeue(TOperation operation)
            {
                // Insert at the head of the queue
                Debug.Assert(!IsStopped, "Expected !IsStopped");
                Debug.Assert(operation.Next == null, "Operation already in queue");

                if (IsEmpty)
                {
                    operation.Next = operation;
                    _tail = operation;
                }
                else
                {
                    operation.Next = _tail.Next;
                    _tail.Next = operation;
                }
            }
#endif

            public void Complete(SocketAsyncContext context)
            {
                AsyncOperation op;
                using (Lock())
                {
#if TRACE
                    Trace($"{QueueId(context)}: Enter Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif

                    if (IsStopped)
                    {
#if TRACE
                        Trace($"{QueueId(context)}: Leave Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif
                        return;
                    }

                    if (_tail == null)
                    {
                        // Queue is empty
                        // Set state to QueueState.Set to allow callers to try to perform I/O immediately
                        State = QueueState.Set;

#if TRACE
                        Trace($"{QueueId(context)}: Leave Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif
                        return;
                    }

                    op = _tail.Next;        // head of queue
                }

                while (op.TryCompleteAsync(context))
                {
                    using (Lock())
                    {
                        if (IsStopped)
                        {
#if TRACE
                            Trace($"{QueueId(context)}: Leave Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif
                            return;
                        }

                        if (op == _tail)
                        {
                            // No more operations to process
                            _tail = null;

                            // Set state to QueueState.Set to allow callers to try to perform I/O immediately
                            State = QueueState.Set;
#if TRACE
                            Trace($"{QueueId(context)}: Leave Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif
                            return;
                        }
                        else
                        {
                            // Pop current operation and advance to next
                            op = _tail.Next = op.Next;
                        }
                    }
                }

#if TRACE
                Trace($"{QueueId(context)}: Leave Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif
            }

            public void StopAndAbort(SocketAsyncContext context)
            {
                // We should be called exactly once, by SafeCloseSocket.
                Debug.Assert(State != QueueState.Stopped);

                using (Lock())
                {
                    Debug.Assert(State != QueueState.Stopped);
#if TRACE
                    Trace($"{QueueId(context)}: Enter StopAndAbort, State={this.State}, IsEmpty={this.IsEmpty}");
#endif

                    if (State == QueueState.Stopped)
                    {
                        // Already stopped, don''t need to do it again
                        return;
                    }

                    State = QueueState.Stopped;

                    if (_tail != null)
                    {
                        AsyncOperation op = _tail;
                        do
                        {
                            op.TryCancel();
                            op = op.Next;
                        } while (op != _tail);
                    }

#if TRACE
                    Trace($"{IdOf(context)}: Leave Complete, State={this.State}, IsEmpty={this.IsEmpty}");
#endif
                }
            }

#if TRACE
            public string QueueType =>
                typeof(TOperation) == typeof(ReadOperation) ? "recv" :
                typeof(TOperation) == typeof(WriteOperation) ? "send" :
                "";

            public string QueueId(SocketAsyncContext context) => $"{IdOf(context)}-{QueueType}";
#endif
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

#if false   // For the future
        // I can't move the lock taking here yet
        // But, evetually...
        private void PerformSyncOperation<TOperation>(ref OperationQueue<TOperation> queue, TOperation operation, int timeout, bool maintainOrder)
            where TOperation : AsyncOperation
        {
            // CONSIDER: Alloc wait object here

            using (queue.Lock())
            {
                if (!StartAsyncOperation(ref queue, operation, maintainOrder: false))
                {
                    // Completed synchronously
                    return;
                }
            }

            // CONSIDER: Move Wait logic here, only place it's called, I think
            if (!operation.Wait(timeout))
            {
                // CONSIDER: DoAbort could take an errorCode
                operation.DoAbort();
                operation.ErrorCode = SocketError.TimedOut;
            }
        }
#endif

        //  Maybe rename for PerformAsyncOperation, like above
        // Return true for pending, false for completed sync (incl failure and abort)
        private bool StartAsyncOperation<TOperation>(ref OperationQueue<TOperation> queue, TOperation operation, bool maintainOrder)
            where TOperation : AsyncOperation
        {
            // TODO: Shouldn't be locked here
            Debug.Assert(queue.IsLocked);

            bool isStopped;
            while (true)
            {
                if (TryBeginOperation(ref queue, operation, maintainOrder, isStopped: out isStopped))
                {
                    // Successfully enqueued
                    return true;
                }

                if (isStopped)
                {
                    operation.DoAbort();
                    return false;
                }

                // Try sync
                if (operation.TryComplete(this))
                {
                    return false;
                }
            }
        }

        // This is the old "TryBeginOperation"; need to rewrite, rename, put on queue struct
        private bool TryBeginOperation<TOperation>(ref OperationQueue<TOperation> queue, TOperation operation, bool maintainOrder, out bool isStopped)
            where TOperation : AsyncOperation
        {
            // TODO: Push queue locking into queue class
            Debug.Assert(queue.IsLocked);

            // TODO: This should happen outside of queue lock.
            if (!_registered)
            {
                Register();
            }

#if TRACE
            Trace($"{queue.QueueId(this)}: Enter TryBeginOperation for {IdOf(operation)}, State={queue.State}, IsEmpty={queue.IsEmpty}, maintainOrder={maintainOrder}");
#endif

            switch (queue.State)
            {
                case QueueState.Stopped:
                    isStopped = true;
                    return false;

                case QueueState.Clear:
                    break;

                case QueueState.Set:
                    if (queue.IsEmpty || !maintainOrder)
                    {
                        isStopped = false;
                        queue.State = QueueState.Clear;
                        return false;
                    }
                    break;
            }

            queue.Enqueue(operation);

#if TRACE
            Trace($"{IdOf(this)}: Enqueue {IdOf(operation)}, State={queue.State}, IsEmpty={queue.IsEmpty}");
#endif

            isStopped = false;
            return true;
        }

        public SocketError Accept(byte[] socketAddress, ref int socketAddressLen, int timeout, out IntPtr acceptedFd)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketError errorCode;
            if (SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
            {
                Debug.Assert(errorCode == SocketError.Success || acceptedFd == (IntPtr)(-1), $"Unexpected values: errorCode={errorCode}, acceptedFd={acceptedFd}");
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new AcceptOperation {
                    Event = @event,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen
                };

                using (_receiveQueue.Lock())
                {
                    if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: false))
                    {
                        socketAddressLen = operation.SocketAddressLen;
                        acceptedFd = operation.AcceptedFileDescriptor;
                        return operation.ErrorCode;
                    }
                }

                if (!operation.Wait(timeout))
                {
                    acceptedFd = (IntPtr)(-1);
                    return SocketError.TimedOut;
                }

                socketAddressLen = operation.SocketAddressLen;
                acceptedFd = operation.AcceptedFileDescriptor;
                return operation.ErrorCode;
            }
        }

        public SocketError AcceptAsync(byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd, Action<IntPtr, byte[], int, SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            SetNonBlocking();

            SocketError errorCode;
            if (SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
            {
                Debug.Assert(errorCode == SocketError.Success || acceptedFd == (IntPtr)(-1), $"Unexpected values: errorCode={errorCode}, acceptedFd={acceptedFd}");

                return errorCode;
            }

            var operation = new AcceptOperation {
                Callback = callback,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            using (_receiveQueue.Lock())
            {
                if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: false))
                {
                    socketAddressLen = operation.SocketAddressLen;
                    acceptedFd = operation.AcceptedFileDescriptor;
                    return operation.ErrorCode;
                }
            }

            return SocketError.IOPending;
        }

        public SocketError Connect(byte[] socketAddress, int socketAddressLen, int timeout)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketError errorCode;
            if (SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                _socket.RegisterConnectResult(errorCode);
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new ConnectOperation {
                    Event = @event,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen
                };

                using (_sendQueue.Lock())
                {
                    if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: false))
                    {
                        return operation.ErrorCode;
                    }
                }

                return operation.Wait(timeout) ? operation.ErrorCode : SocketError.TimedOut;
            }
        }

        public SocketError ConnectAsync(byte[] socketAddress, int socketAddressLen, Action<SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            SetNonBlocking();

            SocketError errorCode;
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

            using (_sendQueue.Lock())
            {
                if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: false))
                {
                    return operation.ErrorCode;
                }
            }

            return SocketError.IOPending;
        }

        public SocketError Receive(byte[] buffer, int offset, int count, ref SocketFlags flags, int timeout, out int bytesReceived)
        {
            int socketAddressLen = 0;
            return ReceiveFrom(buffer, offset, count, ref flags, null, ref socketAddressLen, timeout, out bytesReceived);
        }

        public SocketError Receive(Span<byte> buffer, ref SocketFlags flags, int timeout, out int bytesReceived)
        {
            int socketAddressLen = 0;
            return ReceiveFrom(buffer, ref flags, null, ref socketAddressLen, timeout, out bytesReceived);
        }

        public SocketError ReceiveAsync(byte[] buffer, int offset, int count, SocketFlags flags, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            int socketAddressLen = 0;
            return ReceiveFromAsync(buffer, offset, count, flags, null, ref socketAddressLen, out bytesReceived, out receivedFlags, callback);
        }

        public SocketError ReceiveFrom(byte[] buffer, int offset, int count, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, int timeout, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            ManualResetEventSlim @event = null;
            try
            {
                ReceiveOperation operation;
                using (_receiveQueue.Lock())
                {
                    SocketFlags receivedFlags;
                    SocketError errorCode;

                    if (_receiveQueue.IsEmpty &&
                        SocketPal.TryCompleteReceiveFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                    {
                        flags = receivedFlags;
                        return errorCode;
                    }

                    @event = new ManualResetEventSlim(false, 0);

                    operation = new BufferArrayReceiveOperation
                    {
                        Event = @event,
                        Buffer = buffer,
                        Offset = offset,
                        Count = count,
                        Flags = flags,
                        SocketAddress = socketAddress,
                        SocketAddressLen = socketAddressLen,
                    };

                    if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: true))
                    {
                        flags = operation.ReceivedFlags;
                        bytesReceived = operation.BytesTransferred;
                        return operation.ErrorCode;
                    }
                }

                bool signaled = operation.Wait(timeout);
                socketAddressLen = operation.SocketAddressLen;
                flags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                return signaled ? operation.ErrorCode : SocketError.TimedOut;
            }
            finally
            {
                @event?.Dispose();
            }
        }

        public unsafe SocketError ReceiveFrom(Span<byte> buffer, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, int timeout, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            fixed (byte* bufferPtr = &buffer.DangerousGetPinnableReference())
            {
                ManualResetEventSlim @event = null;
                try
                {
                    ReceiveOperation operation;
                    lock (_receiveQueue.QueueLock)
                    {
                        SocketFlags receivedFlags;
                        SocketError errorCode;

                        if (_receiveQueue.IsEmpty &&
                            SocketPal.TryCompleteReceiveFrom(_socket, buffer, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                        {
                            flags = receivedFlags;
                            return errorCode;
                        }

                        @event = new ManualResetEventSlim(false, 0);

                        operation = new BufferPtrReceiveOperation
                        {
                            Event = @event,
                            BufferPtr = bufferPtr,
                            Length = buffer.Length,
                            Flags = flags,
                            SocketAddress = socketAddress,
                            SocketAddressLen = socketAddressLen,
                        };

                        bool isStopped;
                        while (!TryBeginOperation(ref _receiveQueue, operation, Interop.Sys.SocketEvents.Read, maintainOrder: true, isStopped: out isStopped))
                        {
                            if (isStopped)
                            {
                                flags = operation.ReceivedFlags;
                                bytesReceived = operation.BytesTransferred;
                                return SocketError.Interrupted;
                            }

                            if (operation.TryComplete(this))
                            {
                                socketAddressLen = operation.SocketAddressLen;
                                flags = operation.ReceivedFlags;
                                bytesReceived = operation.BytesTransferred;
                                return operation.ErrorCode;
                            }
                        }
                    }

                    bool signaled = operation.Wait(timeout);
                    socketAddressLen = operation.SocketAddressLen;
                    flags = operation.ReceivedFlags;
                    bytesReceived = operation.BytesTransferred;
                    return signaled ? operation.ErrorCode : SocketError.TimedOut;
                }
                finally
                {
                    @event?.Dispose();
                }
            }
        }


        public SocketError ReceiveFromAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            using (_receiveQueue.Lock())
            {
                SocketError errorCode;

                if (_receiveQueue.IsEmpty &&
                    SocketPal.TryCompleteReceiveFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                {
                    // Synchronous success or failure
                    return errorCode;
                }

                var operation = new BufferArrayReceiveOperation
                {
                    Callback = callback,
                    Buffer = buffer,
                    Offset = offset,
                    Count = count,
                    Flags = flags,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen,
                };

                if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: true))
                {
                    receivedFlags = operation.ReceivedFlags;
                    bytesReceived = operation.BytesTransferred;
                    return operation.ErrorCode;
                }

                bytesReceived = 0;
                receivedFlags = SocketFlags.None;
                return SocketError.IOPending;
            }
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

            ManualResetEventSlim @event = null;
            try
            {
                ReceiveOperation operation;

                using (_receiveQueue.Lock())
                {
                    SocketFlags receivedFlags;
                    SocketError errorCode;
                    if (_receiveQueue.IsEmpty &&
                        SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                    {
                        flags = receivedFlags;
                        return errorCode;
                    }

                    @event = new ManualResetEventSlim(false, 0);

                    operation = new BufferListReceiveOperation
                    {
                        Event = @event,
                        Buffers = buffers,
                        Flags = flags,
                        SocketAddress = socketAddress,
                        SocketAddressLen = socketAddressLen,
                    };

                    if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: true))
                    {
                        socketAddressLen = operation.SocketAddressLen;
                        flags = operation.ReceivedFlags;
                        bytesReceived = operation.BytesTransferred;
                        return operation.ErrorCode;
                    }
                }

                bool signaled = operation.Wait(timeout);
                socketAddressLen = operation.SocketAddressLen;
                flags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                return signaled ? operation.ErrorCode : SocketError.TimedOut;
            }
            finally
            {
                @event?.Dispose();
            }
        }

        public SocketError ReceiveFromAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            ReceiveOperation operation;

            using (_receiveQueue.Lock())
            {
                SocketError errorCode;
                if (_receiveQueue.IsEmpty &&
                    SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
                {
                    // Synchronous success or failure
                    return errorCode;
                }

                operation = new BufferListReceiveOperation
                {
                    Callback = callback,
                    Buffers = buffers,
                    Flags = flags,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen,
                };

                if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: true))
                {
                    socketAddressLen = operation.SocketAddressLen;
                    receivedFlags = operation.ReceivedFlags;
                    bytesReceived = operation.BytesTransferred;
                    return operation.ErrorCode;
                }

                bytesReceived = 0;
                receivedFlags = SocketFlags.None;
                return SocketError.IOPending;
            }
        }

        public SocketError ReceiveMessageFrom(
            byte[] buffer, IList<ArraySegment<byte>> buffers, int offset, int count, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, int timeout, out IPPacketInformation ipPacketInformation, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            ManualResetEventSlim @event = null;
            try
            {
                ReceiveMessageFromOperation operation;

                using (_receiveQueue.Lock())
                {
                    SocketFlags receivedFlags;
                    SocketError errorCode;
                    if (_receiveQueue.IsEmpty &&
                        SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer, buffers, offset, count, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
                    {
                        flags = receivedFlags;
                        return errorCode;
                    }

                    @event = new ManualResetEventSlim(false, 0);

                    operation = new ReceiveMessageFromOperation
                    {
                        Event = @event,
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

                    if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: true))
                    {
                        socketAddressLen = operation.SocketAddressLen;
                        flags = operation.ReceivedFlags;
                        ipPacketInformation = operation.IPPacketInformation;
                        bytesReceived = operation.BytesTransferred;
                        return operation.ErrorCode;
                    }
                }

                bool signaled = operation.Wait(timeout);
                socketAddressLen = operation.SocketAddressLen;
                flags = operation.ReceivedFlags;
                ipPacketInformation = operation.IPPacketInformation;
                bytesReceived = operation.BytesTransferred;
                return signaled ? operation.ErrorCode : SocketError.TimedOut;
            }
            finally
            {
                @event?.Dispose();
            }
        }

        public SocketError ReceiveMessageFromAsync(byte[] buffer, IList<ArraySegment<byte>> buffers, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, out int bytesReceived, out SocketFlags receivedFlags, out IPPacketInformation ipPacketInformation, Action<int, byte[], int, SocketFlags, IPPacketInformation, SocketError> callback)
        {
            SetNonBlocking();

            using (_receiveQueue.Lock())
            {
                SocketError errorCode;

                if (_receiveQueue.IsEmpty &&
                    SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer, buffers, offset, count, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
                {
                    // Synchronous success or failure
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

                if (!StartAsyncOperation(ref _receiveQueue, operation, maintainOrder: true))
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
        }

        public SocketError Send(ReadOnlySpan<byte> buffer, SocketFlags flags, int timeout, out int bytesSent) =>
            SendTo(buffer, flags, null, 0, timeout, out bytesSent);

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

            ManualResetEventSlim @event = null;
            try
            {
                BufferArraySendOperation operation;

                using (_sendQueue.Lock())
                {
                    bytesSent = 0;
                    SocketError errorCode;

                    if (_sendQueue.IsEmpty &&
                        SocketPal.TryCompleteSendTo(_socket, buffer, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                    {
                        return errorCode;
                    }

                    @event = new ManualResetEventSlim(false, 0);

                    operation = new BufferArraySendOperation
                    {
                        Event = @event,
                        Buffer = buffer,
                        Offset = offset,
                        Count = count,
                        Flags = flags,
                        SocketAddress = socketAddress,
                        SocketAddressLen = socketAddressLen,
                        BytesTransferred = bytesSent
                    };

                    if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: true))
                    {
                        bytesSent = operation.BytesTransferred;
                        return operation.ErrorCode;
                    }
                }

                bool signaled = operation.Wait(timeout);
                bytesSent = operation.BytesTransferred;
                return signaled ? operation.ErrorCode : SocketError.TimedOut;
            }
            finally
            {
                @event?.Dispose();
            }
        }

        public unsafe SocketError SendTo(ReadOnlySpan<byte> buffer, SocketFlags flags, byte[] socketAddress, int socketAddressLen, int timeout, out int bytesSent)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            fixed (byte* bufferPtr = &buffer.DangerousGetPinnableReference())
            {
                ManualResetEventSlim @event = null;
                try
                {
                    BufferPtrSendOperation operation;

                    lock (_sendQueue.QueueLock)
                    {
                        bytesSent = 0;
                        SocketError errorCode;

                        int bufferIndexIgnored = 0, offset = 0, count = buffer.Length;
                        if (_sendQueue.IsEmpty &&
                            SocketPal.TryCompleteSendTo(_socket, buffer, null, ref bufferIndexIgnored, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                        {
                            return errorCode;
                        }

                        @event = new ManualResetEventSlim(false, 0);

                        operation = new BufferPtrSendOperation
                        {
                            Event = @event,
                            BufferPtr = bufferPtr,
                            Offset = offset,
                            Count = count,
                            Flags = flags,
                            SocketAddress = socketAddress,
                            SocketAddressLen = socketAddressLen,
                            BytesTransferred = bytesSent
                        };

                        bool isStopped;
                        while (!TryBeginOperation(ref _sendQueue, operation, Interop.Sys.SocketEvents.Write, maintainOrder: true, isStopped: out isStopped))
                        {
                            if (isStopped)
                            {
                                bytesSent = operation.BytesTransferred;
                                return SocketError.Interrupted;
                            }

                            if (operation.TryComplete(this))
                            {
                                bytesSent = operation.BytesTransferred;
                                return operation.ErrorCode;
                            }
                        }
                    }

                    bool signaled = operation.Wait(timeout);
                    bytesSent = operation.BytesTransferred;
                    return signaled ? operation.ErrorCode : SocketError.TimedOut;
                }
                finally
                {
                    @event?.Dispose();
                }
            }
        }

        public SocketError SendToAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            using (_sendQueue.Lock())
            {
                bytesSent = 0;
                SocketError errorCode;

                if (_sendQueue.IsEmpty &&
                    SocketPal.TryCompleteSendTo(_socket, buffer, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                {
                    // Synchronous success or failure
                    return errorCode;
                }

                var operation = new BufferArraySendOperation
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

                if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: true))
                {
                    bytesSent = operation.BytesTransferred;
                    return operation.ErrorCode;
                }
                
                return SocketError.IOPending;
            }
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

            ManualResetEventSlim @event = null;
            try
            {
                BufferListSendOperation operation;

                using (_sendQueue.Lock())
                {
                    bytesSent = 0;
                    int bufferIndex = 0;
                    int offset = 0;
                    SocketError errorCode;

                    if (_sendQueue.IsEmpty &&
                        SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                    {
                        return errorCode;
                    }

                    @event = new ManualResetEventSlim(false, 0);

                    operation = new BufferListSendOperation
                    {
                        Event = @event,
                        Buffers = buffers,
                        BufferIndex = bufferIndex,
                        Offset = offset,
                        Flags = flags,
                        SocketAddress = socketAddress,
                        SocketAddressLen = socketAddressLen,
                        BytesTransferred = bytesSent
                    };

                    if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: true))
                    {
                        bytesSent = operation.BytesTransferred;
                        return operation.ErrorCode;
                    }
                }

                bool signaled = operation.Wait(timeout);
                bytesSent = operation.BytesTransferred;
                return signaled ? operation.ErrorCode : SocketError.TimedOut;
            }
            finally
            {
                @event?.Dispose();
            }
        }

        public SocketError SendToAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            using (_sendQueue.Lock())
            {
                bytesSent = 0;
                int bufferIndex = 0;
                int offset = 0;
                SocketError errorCode;

                if (_sendQueue.IsEmpty &&
                    SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
                {
                    // Synchronous success or failure
                    return errorCode;
                }

                var operation = new BufferListSendOperation
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

                if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: true))
                {
                    bytesSent = operation.BytesTransferred;
                    return operation.ErrorCode;
                }

                return SocketError.IOPending;
            }
        }

        public SocketError SendFile(SafeFileHandle fileHandle, long offset, long count, int timeout, out long bytesSent)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            ManualResetEventSlim @event = null;
            try
            {
                SendFileOperation operation;

                using (_sendQueue.Lock())
                {
                    bytesSent = 0;
                    SocketError errorCode;

                    if (_sendQueue.IsEmpty &&
                        SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
                    {
                        return errorCode;
                    }

                    @event = new ManualResetEventSlim(false, 0);

                    operation = new SendFileOperation
                    {
                        Event = @event,
                        FileHandle = fileHandle,
                        Offset = offset,
                        Count = count,
                        BytesTransferred = bytesSent
                    };

                    if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: true))
                    {
                        bytesSent = operation.BytesTransferred;
                        return operation.ErrorCode;
                    }
                }

                bool signaled = operation.Wait(timeout);
                bytesSent = operation.BytesTransferred;
                return signaled ? operation.ErrorCode : SocketError.TimedOut;
            }
            finally
            {
                @event?.Dispose();
            }
        }

        public SocketError SendFileAsync(SafeFileHandle fileHandle, long offset, long count, out long bytesSent, Action<long, SocketError> callback)
        {
            SetNonBlocking();

            using (_sendQueue.Lock())
            {
                bytesSent = 0;
                SocketError errorCode;

                if (_sendQueue.IsEmpty &&
                    SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
                {
                    // Synchronous success or failure
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

                if (!StartAsyncOperation(ref _sendQueue, operation, maintainOrder: true))
                {
                    bytesSent = operation.BytesTransferred;
                    return operation.ErrorCode;
                }

                return SocketError.IOPending;
            }
        }

        public unsafe void HandleEvents(Interop.Sys.SocketEvents events)
        {
            if ((events & Interop.Sys.SocketEvents.Error) != 0)
            {
                // Set the Read and Write flags as well; the processing for these events
                // will pick up the error.
                events |= Interop.Sys.SocketEvents.Read | Interop.Sys.SocketEvents.Write;
            }

            if ((events & Interop.Sys.SocketEvents.Read) != 0)
            {
                _receiveQueue.Complete(this);
            }

            if ((events & Interop.Sys.SocketEvents.Write) != 0)
            {
                _sendQueue.Complete(this);
            }
        }

#if TRACE
        public static void Trace(string s)
        {
            Console.WriteLine(s);
        }

        public static string IdOf(object o) => $"{o.GetType().Name}#{o.GetHashCode():X2}";
#endif
    }
}
