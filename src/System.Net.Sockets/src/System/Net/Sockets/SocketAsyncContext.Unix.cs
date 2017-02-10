// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
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

                return DoTryComplete(context);
            }

            public bool TryCompleteAsync(SocketAsyncContext context)
            {
                return TryCompleteOrAbortAsync(context, abort: false);
            }

            public void AbortAsync()
            {
                bool completed = TryCompleteOrAbortAsync(null, abort: true);
                Debug.Assert(completed, $"Expected TryCompleteOrAbortAsync to return true");
            }

            private bool TryCompleteOrAbortAsync(SocketAsyncContext context, bool abort)
            {
                Debug.Assert(context != null || abort, $"Unexpected values: context={context}, abort={abort}");

                State oldState = (State)Interlocked.CompareExchange(ref _state, (int)State.Running, (int)State.Waiting);
                if (oldState == State.Cancelled)
                {
                    // This operation has been cancelled. The canceller is responsible for
                    // correctly updating any state that would have been handled by
                    // AsyncOperation.Abort.
                    return true;
                }

                Debug.Assert(oldState != State.Complete && oldState != State.Running, $"Unexpected oldState: {oldState}");

                bool completed;
                if (abort)
                {
                    Abort();
                    ErrorCode = SocketError.OperationAborted;
                    completed = true;
                }
                else
                {
                    completed = DoTryComplete(context);
                }

                if (completed)
                {
                    var @event = CallbackOrEvent as ManualResetEventSlim;
                    if (@event != null)
                    {
                        @event.Set();
                    }
                    else
                    {
                        Debug.Assert(_state != (int)State.Cancelled, $"Unexpected _state: {_state}");
#if DEBUG
                        Debug.Assert(Interlocked.CompareExchange(ref _callbackQueued, 1, 0) == 0, $"Unexpected _callbackQueued: {_callbackQueued}");
#endif

                        ThreadPool.QueueUserWorkItem(o => ((AsyncOperation)o).InvokeCallback(), this);
                    }

                    Volatile.Write(ref _state, (int)State.Complete);
                    return true;
                }

                Volatile.Write(ref _state, (int)State.Waiting);
                return false;
            }

            public bool Wait(int timeout)
            {
                if (Event.Wait(timeout))
                {
                    return true;
                }

                var spinWait = new SpinWait();
                for (;;)
                {
                    int state = Interlocked.CompareExchange(ref _state, (int)State.Cancelled, (int)State.Waiting);
                    switch ((State)state)
                    {
                        case State.Running:
                            // A completion attempt is in progress. Keep busy-waiting.
                            spinWait.SpinOnce();
                            break;

                        case State.Complete:
                            // A completion attempt succeeded. Consider this operation as having completed within the timeout.
                            return true;

                        case State.Waiting:
                            // This operation was successfully cancelled.
                            return false;
                    }
                }
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
                return SocketPal.TryCompleteReceiveMessageFrom(context._socket, Buffer, Offset, Count, Flags, SocketAddress, ref SocketAddressLen, IsIPv4, IsIPv6, out BytesTransferred, out ReceivedFlags, out IPPacketInformation, out ErrorCode);
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
                context.RegisterConnectResult(ErrorCode);
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

        private struct OperationQueue<TOperation>
            where TOperation : AsyncOperation
        {
            private enum QueueState
            {
                Empty = 0,          // Nothing in the queues
                Pending = 1,        // Queue has entries and is waiting for epoll notification
                Processing = 2,     // Queue entries are being processed
                Stopped = 3,        // Queue has been stopped and no further I/O can occur
                                    // Note, we may still be draining entries from the queue and cancelling them
            }

            // TODO: Make this a SpinLock
            private object _queueLock;
            private QueueState _queueState;
            private int _sequenceNumber;
            
            // Tail entry in queue.  Note the list is circular, so head = _tail.Next.
            private TOperation _tail;


            public void Init()
            {
                // TODO: Would be nice if this was the constructor.
                // TODO: Limit access to this, and make it a spin lock
                _queueLock = new object();
            }

            [Conditional("DEBUG")]
            private void CheckQueueState()
            {
                Debug.Assert(Monitor.IsEntered(_queueLock));
                switch (_queueState)
                {
                    case QueueState.Empty:
                    case QueueState.Stopped:
                        Debug.Assert(_tail == null);
                        break;

                    case QueueState.Processing:
                    case QueueState.Pending:
                        Debug.Assert(_tail != null);
                        break;

                    default:
                        Debug.Assert(false);
                        break;
                }
            } 

            // CONSIDER: Change this, and other places, to switch on state, and remove CheckQueueState above


            public bool CanTryOperation(bool maintainOrder, out int observedSequenceNumber)
            {
                lock (_queueLock)
                {
                    CheckQueueState();

                    // Remember the queue sequence number, to detect cases where
                    // we need to retry the operation in EnqueueOperation below.
                    observedSequenceNumber = _sequenceNumber;

                    // It's ok to try to to execute the operation on the caller thread if either
                    // the queue is empty, or we don't care about ordering (i.e. Accept).
                    // Otherwise, we need to preserve ordering and can't try the operation in the caller.
                    return (_queueState == QueueState.Empty || (!maintainOrder && _queueState == QueueState.Processing));
                }
            }

            // Due to retry logic, this may end up actually completing the operation.
            // Returns true if the operation was enqueued, false if it was actually completed here.
            public bool EnqueueOperation(SocketAsyncContext context, TOperation operation, int observedSequenceNumber, out bool isStopped)
            {
                isStopped = false;
                while (true)
                {
                    lock (_queueLock)
                    {
                        CheckQueueState();

                        if (_queueState == QueueState.Stopped)
                        {
                            isStopped = true;
                            return false;
                        }

                        // We want to enqueue here *unless* the following two things are true:
                        // (1) The queue is empty.
                        // (2) The sequence number has changed.
                        // If these are both true, then a notification has come in and been processed
                        // since last we checked.
                        // Thus we need to retry here before enqueuing, or we may never get another notification.
                        if (_queueState == QueueState.Empty)
                        {
                            if (observedSequenceNumber == _sequenceNumber)
                            {
                                // No new notification seen, ok to queue.
                                operation.Next = operation;
                                _tail = operation;
                                _queueState = QueueState.Pending;
                                return true;
                            }
                            else
                            {
                                // Need to retry the operation.
                                // Update our sequence number for the next go-around.
                                observedSequenceNumber = _sequenceNumber;
                            }
                        }
                        else
                        {
                            // Pending or Processing, so enqueue
                            operation.Next = _tail.Next;
                            _tail.Next = operation;
                            _tail = operation;
                            return true;
                        }
                    }

                    // Retry the operation.
                    if (operation.TryComplete(context))
                    {
                        // Operation completed.
                        return false;
                    }
                }
            }

#if false
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
#endif
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

#if false // temporary
            // This is the version of HandleEvent with fine locking
            public void HandleEvent(SocketAsyncContext context)
            {
                AsyncOperation op; 
                lock (_queueLock)
                {
                    if (_queueState == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        return;
                    }

                    if (_queueState == QueueState.Empty)
                    {
                        Debug.Assert(_tail == null);
                        _sequenceNumber++;
                        return;
                    }
                    
                    // Shouldn't be processing at this point
                    Debug.Assert(_queueState == QueueState.Pending);

                    _queueState = QueueState.Processing;
                    
                    // Retrieve head of queue (in _tail.Next) for processing.                    
                    // Head is tail.Next.
                    op = _tail.Next;
                }

                while (true)
                {
                    if (!op.TryCompleteAsync(context))
                    {
                        // Operation returned EWOULDBLOCK.
                        // We can't process any more operations.
                        
                        lock (_queueLock)
                        {
                            if (_queueState == QueueState.Stopped)
                            {
                                // Queue has been stopped.  Exit lock and abort below.
                                Debug.Assert(_tail == null);
                            }
                            else
                            {
                                // Queue is still running.  Wait for the next epoll notification.
                                Debug.Assert(op == _tail.Next);
                                Debug.Assert(_queueState == QueueState.Processing);
                                
                                _queueState = QueueState.Pending;
                                return;
                            }
                        }
                        
                        // Queue is stopped.  Abort this op.
                        op.AbortAsync();
                        return;
                    }

                    // Operation was successfully completed.
                    // Remove it from the queue and see if there are any more entries to process.
                    lock (_queueLock)
                    {
                        if (_queueState == QueueState.Stopped)
                        {
                            Debug.Assert(_tail == null);
                            return;
                        }
                        
                        // Head should not have changed.
                        Debug.Assert(_queueState == QueueState.Processing);
                        Debug.Assert(_tail != null);
                        Debug.Assert(_tail.Next != null);
                        Debug.Assert(op == _tail.Next);

                        if (op == _tail)
                        {
                            // List had only one element in it, now it's empty
                            _tail = null;
                            _queueState = QueueState.Empty;
                            _sequenceNumber++;
                            return;
                        }

                        // Remove the operation we just completed
                        op = op.Next;
                        _tail.Next = op;
                    }
                }
            }
#endif

            public void HandleEvent(SocketAsyncContext context)
            {
                lock (_queueLock)
                {
                    _sequenceNumber++;

                    if (_queueState == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        return;
                    }

                    if (_queueState == QueueState.Empty)
                    {
                        Debug.Assert(_tail == null);
                        return;
                    }

                    if (_queueState == QueueState.Processing)
                    {
                        // Already processing.  Don't send off another thread.
                        return;
                    }                    
                    
                    // Shouldn't be processing at this point
                    Debug.Assert(_queueState == QueueState.Pending);

                    _queueState = QueueState.Processing;
                }

                ThreadPool.QueueUserWorkItem(ProcessQueueCallback, context);
            }

            private static void ProcessQueueCallback(object o)
            {
                SocketAsyncContext context = (SocketAsyncContext)o;

                Debug.Assert(typeof(TOperation) == typeof(ReadOperation) || typeof(TOperation) == typeof(WriteOperation));

                if (typeof(TOperation) == typeof(ReadOperation))
                {
                    context._receiveQueue.ProcessQueue(context);
                }
                else
                {
                    context._sendQueue.ProcessQueue(context);
                }

            }
            public void ProcessQueue(SocketAsyncContext context)
            {
                AsyncOperation op; 
                lock (_queueLock)
                {
                    if (_queueState == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        return;
                    }

                    // We should be in processing state, unless we stopped
                    Debug.Assert(_queueState == QueueState.Processing);

                    // Retrieve head of queue (in _tail.Next) for processing.                    
                    // Head is tail.Next.
                    op = _tail.Next;

                    while (true)
                    {
                        if (!op.TryCompleteAsync(context))
                        {
                            // Operation returned EWOULDBLOCK.
                            // We can't process any more operations.
                        
                            if (_queueState == QueueState.Stopped)
                            {
                                // Queue has been stopped.  Exit lock and abort below.
                                Debug.Assert(_tail == null);
                            }
                            else
                            {
                                // Queue is still running.  Wait for the next epoll notification.
                                Debug.Assert(op == _tail.Next);
                                Debug.Assert(_queueState == QueueState.Processing);
                                
                                _queueState = QueueState.Pending;
                                return;
                            }
                        
                            // Queue is stopped.  Abort this op.
                            op.AbortAsync();
                            return;
                        }

                        // Operation was successfully completed.
                        // Remove it from the queue and see if there are any more entries to process.
                        if (_queueState == QueueState.Stopped)
                        {
                            Debug.Assert(_tail == null);
                            return;
                        }
                        
                        // Head should not have changed.
                        Debug.Assert(_queueState == QueueState.Processing);
                        Debug.Assert(_tail != null);
                        Debug.Assert(_tail.Next != null);
                        Debug.Assert(op == _tail.Next);

                        if (op == _tail)
                        {
                            // List had only one element in it, now it's empty
                            _tail = null;
                            _queueState = QueueState.Empty;
//                            _sequenceNumber++;
                            return;
                        }

                        // Remove the operation we just completed
                        op = op.Next;
                        _tail.Next = op;
                    }
                }
            }
            
            public void StopAndAbort()
            {
                AsyncOperation head;
                lock (_queueLock)
                {
//                    Debug.Assert(_queueState != QueueState.Stopped);
                    if (_queueState == QueueState.Stopped)
                    {
                        return;
                    }

                    if (_queueState == QueueState.Empty)
                    {
                        Debug.Assert(_tail == null);
                        _queueState = QueueState.Stopped;
                        return;
                    }
                    
                    Debug.Assert(_tail != null);

                    // Grab queue list and clear it, so we can abort them below                    
                    head = _tail.Next;
                    
                    // If we are currently processing, then the processing thread is trying to perform
                    // the operation at the head of the queue.  Skip it here.
                    
                    if (_queueState == QueueState.Processing)
                    {
                        head = (head == _tail) ? null : head.Next;
                    }
                    
                    _tail.Next = null;
                    _tail = null;
                    
                    _queueState = QueueState.Stopped;
                }

                // Abort unprocessed requests                
                while (head != null)
                {
                    head.AbortAsync();
                    head = head.Next;
                }
            }
        }

        private SafeCloseSocket _socket;
        private OperationQueue<ReadOperation> _receiveQueue;
        private OperationQueue<WriteOperation> _sendQueue;
        private SocketAsyncEngine.Token _asyncEngineToken;
        private Interop.Sys.SocketEvents _registeredEvents;
        private bool _nonBlockingSet;
        private bool _connectFailed;

        private readonly object _registerLock = new object();

        public SocketAsyncContext(SafeCloseSocket socket)
        {
            _socket = socket;

            _receiveQueue.Init();
            _sendQueue.Init();
        }

        private void Register(Interop.Sys.SocketEvents events)
        {
            lock (_registerLock)
            {
                Debug.Assert((_registeredEvents & events) == Interop.Sys.SocketEvents.None, $"Unexpected values: _registeredEvents={_registeredEvents}, events={events}");

                if (!_asyncEngineToken.WasAllocated)
                {
                    _asyncEngineToken = new SocketAsyncEngine.Token(this);
                }

                events |= _registeredEvents;

                Interop.Error errorCode;
                if (!_asyncEngineToken.TryRegister(_socket, _registeredEvents, events, out errorCode))
                {
                    if (errorCode == Interop.Error.ENOMEM || errorCode == Interop.Error.ENOSPC)
                    {
                        throw new OutOfMemoryException();
                    }
                    else
                    {
                        throw new InternalException();                        
                    }
                }

                _registeredEvents = events;
            }
        }

        public void Close()
        {
            // Drain queues
            _sendQueue.StopAndAbort();
            _receiveQueue.StopAndAbort();

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

        public void CheckForPriorConnectFailure()
        {
            if (_connectFailed)
            {
                throw new PlatformNotSupportedException(SR.net_sockets_connect_multiconnect_notsupported);
            }
        }

        public void RegisterConnectResult(SocketError error)
        {
            if (error != SocketError.Success && error != SocketError.WouldBlock)
            {
                _connectFailed = true;
            }
        }

#if false
        private bool TryBeginOperation<TOperation>(ref OperationQueue<TOperation> queue, TOperation operation, Interop.Sys.SocketEvents events, bool maintainOrder, out bool isStopped)
            where TOperation : AsyncOperation
        {
#if false
            // Exactly one of the two queue locks must be held by the caller
            Debug.Assert(Monitor.IsEntered(_sendQueue.QueueLock) ^ Monitor.IsEntered(_receiveQueue.QueueLock));

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

            if ((_registeredEvents & events) == Interop.Sys.SocketEvents.None)
            {
                Register(events);
            }

            queue.Enqueue(operation);
#endif
            isStopped = false;
            return true;
        }
#endif

        // TODO: Change queue names to read/write

        private bool CanTryReadOperation(bool maintainOrder, out int observedSequenceNumber)
        {
            return _receiveQueue.CanTryOperation(maintainOrder, out observedSequenceNumber);
        }

        private bool CanTryWriteOperation(bool maintainOrder, out int observedSequenceNumber)
        {
            return _sendQueue.CanTryOperation(maintainOrder, out observedSequenceNumber);
        }

        private bool EnqueueReadOperation(ReadOperation operation, int observedSequenceNumber, out bool isStopped)
        {
            if ((_registeredEvents & Interop.Sys.SocketEvents.Read) == Interop.Sys.SocketEvents.None)
            {
                Register(Interop.Sys.SocketEvents.Read);
            }

            return _receiveQueue.EnqueueOperation(this, operation, observedSequenceNumber, out isStopped);
        }

        // CONSIDER: Change this around so this calls something like "ProcessSyncOperation"
        // TODO: Move @event stuff here

        private void ProcessSyncReadOperation(ReadOperation operation, int observedSequenceNumber, int timeout)
        {
            bool isStopped;
            if (EnqueueReadOperation(operation, observedSequenceNumber, out isStopped))
            {
                if (!operation.Wait(timeout))
                {
                    operation.ErrorCode = SocketError.TimedOut;
                }

                // Otherwise, wait succeded and operation is complete
            }
            else
            {
                if (isStopped)
                {
                    operation.ErrorCode = SocketError.Interrupted;
                }

                // Otherwise, operation was completed synchronously on retry
            }
        }
        
        private bool EnqueueAsyncReadOperation(ReadOperation operation, int observedSequenceNumber)
        {
            bool isStopped;
            bool enqueued = EnqueueReadOperation(operation, observedSequenceNumber, out isStopped);

            if (!enqueued && isStopped)
            {
                operation.ErrorCode = SocketError.OperationAborted;
            }

            return enqueued;
        }

        private bool EnqueueWriteOperation(WriteOperation operation, int observedSequenceNumber, out bool isStopped)
        {
            if ((_registeredEvents & Interop.Sys.SocketEvents.Write) == Interop.Sys.SocketEvents.None)
            {
                Register(Interop.Sys.SocketEvents.Write);
            }

            return _sendQueue.EnqueueOperation(this, operation, observedSequenceNumber, out isStopped);
        }

        private void ProcessSyncWriteOperation(WriteOperation operation, int observedSequenceNumber, int timeout)
        {
            bool isStopped;
            if (EnqueueWriteOperation(operation, observedSequenceNumber, out isStopped))
            {
                if (!operation.Wait(timeout))
                {
                    operation.ErrorCode = SocketError.TimedOut;
                }

                // Otherwise, wait succeded and operation is complete
            }
            else
            {
                if (isStopped)
                {
                    operation.ErrorCode = SocketError.Interrupted;
                }

                // Otherwise, operation was completed synchronously on retry
            }
        }
        
        private bool EnqueueAsyncWriteOperation(WriteOperation operation, int observedSequenceNumber)
        {
            bool isStopped;
            bool enqueued = EnqueueWriteOperation(operation, observedSequenceNumber, out isStopped);

            if (!enqueued && isStopped)
            {
                operation.ErrorCode = SocketError.OperationAborted;
            }

            return enqueued;
        }

        public SocketError Accept(byte[] socketAddress, ref int socketAddressLen, int timeout, out IntPtr acceptedFd)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryReadOperation(false, out observedSequenceNumber) &&
                SocketPal.TryCompleteAccept(_socket, socketAddress, ref socketAddressLen, out acceptedFd, out errorCode))
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

                ProcessSyncReadOperation(operation, observedSequenceNumber, timeout);

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
            int observedSequenceNumber;
            if (CanTryReadOperation(false, out observedSequenceNumber) &&
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

            if (EnqueueAsyncReadOperation(operation, observedSequenceNumber))
            {
                acceptedFd = (IntPtr)(-1);
                return SocketError.IOPending;
            }

            socketAddressLen = operation.SocketAddressLen;
            acceptedFd = operation.AcceptedFileDescriptor;
            return operation.ErrorCode;
        }

        public SocketError Connect(byte[] socketAddress, int socketAddressLen, int timeout)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            CheckForPriorConnectFailure();

            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryWriteOperation(false, out observedSequenceNumber) &&
                SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                RegisterConnectResult(errorCode);
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new ConnectOperation {
                    Event = @event,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen
                };

                ProcessSyncWriteOperation(operation, observedSequenceNumber, timeout);

                return operation.ErrorCode;
            }
        }

        public SocketError ConnectAsync(byte[] socketAddress, int socketAddressLen, Action<SocketError> callback)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");
            Debug.Assert(callback != null, "Expected non-null callback");

            CheckForPriorConnectFailure();

            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryWriteOperation(false, out observedSequenceNumber) &&
                SocketPal.TryStartConnect(_socket, socketAddress, socketAddressLen, out errorCode))
            {
                RegisterConnectResult(errorCode);

                return errorCode;
            }

            var operation = new ConnectOperation {
                Callback = callback,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen
            };

            if (EnqueueAsyncWriteOperation(operation, observedSequenceNumber))
            {
                return SocketError.IOPending;
            }

            return operation.ErrorCode;
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
            if (CanTryReadOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                flags = receivedFlags;
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new ReceiveOperation
                {
                    Event = @event,
                    Buffer = buffer,
                    Offset = offset,
                    Count = count,
                    Flags = flags,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen,
                };

                ProcessSyncReadOperation(operation, observedSequenceNumber, timeout);

                socketAddressLen = operation.SocketAddressLen;
                flags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                return operation.ErrorCode;
            }
        }

        public SocketError ReceiveFromAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryReadOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                // Synchronous success or failure
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

            if (EnqueueAsyncReadOperation(operation, observedSequenceNumber))
            {
                bytesReceived = 0;
                receivedFlags = SocketFlags.None;
                return SocketError.IOPending;
            }

            socketAddressLen = operation.SocketAddressLen;
            receivedFlags = operation.ReceivedFlags;
            bytesReceived = operation.BytesTransferred;
            return operation.ErrorCode;
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
            if (CanTryReadOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveFrom(_socket, buffers, flags, socketAddress, ref socketAddressLen, out bytesReceived, out receivedFlags, out errorCode))
            {
                flags = receivedFlags;
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new ReceiveOperation
                {
                    Event = @event,
                    Buffers = buffers,
                    Flags = flags,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen,
                };

                ProcessSyncReadOperation(operation, observedSequenceNumber, timeout);

                socketAddressLen = operation.SocketAddressLen;
                flags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                return operation.ErrorCode;
            }
        }

        public SocketError ReceiveFromAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesReceived, out SocketFlags receivedFlags, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryReadOperation(true, out observedSequenceNumber) &&
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

            if (EnqueueAsyncReadOperation(operation, observedSequenceNumber))
            {
                bytesReceived = 0;
                receivedFlags = SocketFlags.None;
                return SocketError.IOPending;
            }

            socketAddressLen = operation.SocketAddressLen;
            receivedFlags = operation.ReceivedFlags;
            bytesReceived = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError ReceiveMessageFrom(byte[] buffer, int offset, int count, ref SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, int timeout, out IPPacketInformation ipPacketInformation, out int bytesReceived)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            SocketFlags receivedFlags;
            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryReadOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
            {
                flags = receivedFlags;
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new ReceiveMessageFromOperation
                {
                    Event = @event,
                    Buffer = buffer,
                    Offset = offset,
                    Count = count,
                    Flags = flags,
                    SocketAddress = socketAddress,
                    SocketAddressLen = socketAddressLen,
                    IsIPv4 = isIPv4,
                    IsIPv6 = isIPv6,
                };


                ProcessSyncReadOperation(operation, observedSequenceNumber, timeout);

                socketAddressLen = operation.SocketAddressLen;
                flags = operation.ReceivedFlags;
                bytesReceived = operation.BytesTransferred;
                ipPacketInformation = operation.IPPacketInformation;
                return operation.ErrorCode;
            }
        }

        public SocketError ReceiveMessageFromAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, bool isIPv4, bool isIPv6, out int bytesReceived, out SocketFlags receivedFlags, out IPPacketInformation ipPacketInformation, Action<int, byte[],    int, SocketFlags, IPPacketInformation, SocketError> callback)
        {
            SetNonBlocking();

            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryReadOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteReceiveMessageFrom(_socket, buffer, offset, count, flags, socketAddress, ref socketAddressLen, isIPv4, isIPv6, out bytesReceived, out receivedFlags, out ipPacketInformation, out errorCode))
            {
                // Synchronous success or failure
                return errorCode;
            }

            var operation = new ReceiveMessageFromOperation
            {
                Callback = callback,
                Buffer = buffer,
                Offset = offset,
                Count = count,
                Flags = flags,
                SocketAddress = socketAddress,
                SocketAddressLen = socketAddressLen,
                IsIPv4 = isIPv4,
                IsIPv6 = isIPv6,
            };

            if (EnqueueAsyncReadOperation(operation, observedSequenceNumber))
            {
                ipPacketInformation = default(IPPacketInformation);
                bytesReceived = 0;
                receivedFlags = SocketFlags.None;
                return SocketError.IOPending;
            }

            socketAddressLen = operation.SocketAddressLen;
            receivedFlags = operation.ReceivedFlags;
            ipPacketInformation = operation.IPPacketInformation;
            bytesReceived = operation.BytesTransferred;
            return operation.ErrorCode;
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
            if (CanTryWriteOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffer, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new SendOperation
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

                ProcessSyncWriteOperation(operation, observedSequenceNumber, timeout);

                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }
        }

        public SocketError SendToAsync(byte[] buffer, int offset, int count, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryWriteOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffer, ref offset, ref count, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                // Synchronous success or failure
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

            if (EnqueueAsyncWriteOperation(operation, observedSequenceNumber))
            {
                return SocketError.IOPending;
            }

            bytesSent = operation.BytesTransferred;
            return operation.ErrorCode;
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
            if (CanTryWriteOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new SendOperation
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

                ProcessSyncWriteOperation(operation, observedSequenceNumber, timeout);

                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }
        }

        public SocketError SendToAsync(IList<ArraySegment<byte>> buffers, SocketFlags flags, byte[] socketAddress, ref int socketAddressLen, out int bytesSent, Action<int, byte[], int, SocketFlags, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            int bufferIndex = 0;
            int offset = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryWriteOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendTo(_socket, buffers, ref bufferIndex, ref offset, flags, socketAddress, socketAddressLen, ref bytesSent, out errorCode))
            {
                // Synchronous success or failure
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
            };

            if (EnqueueAsyncWriteOperation(operation, observedSequenceNumber))
            {
                return SocketError.IOPending;
            }

            bytesSent = operation.BytesTransferred;
            return operation.ErrorCode;
        }

        public SocketError SendFile(SafeFileHandle fileHandle, long offset, long count, int timeout, out long bytesSent)
        {
            Debug.Assert(timeout == -1 || timeout > 0, $"Unexpected timeout: {timeout}");

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryWriteOperation(true, out observedSequenceNumber) &&
                SocketPal.TryCompleteSendFile(_socket, fileHandle, ref offset, ref count, ref bytesSent, out errorCode))
            {
                return errorCode;
            }

            using (var @event = new ManualResetEventSlim(false, 0))
            {
                var operation = new SendFileOperation
                {
                    Event = @event,
                    FileHandle = fileHandle,
                    Offset = offset,
                    Count = count,
                    BytesTransferred = bytesSent
                };

                ProcessSyncWriteOperation(operation, observedSequenceNumber, timeout);

                bytesSent = operation.BytesTransferred;
                return operation.ErrorCode;
            }
        }

        public SocketError SendFileAsync(SafeFileHandle fileHandle, long offset, long count, out long bytesSent, Action<long, SocketError> callback)
        {
            SetNonBlocking();

            bytesSent = 0;
            SocketError errorCode;
            int observedSequenceNumber;
            if (CanTryWriteOperation(true, out observedSequenceNumber) &&
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

            if (EnqueueAsyncWriteOperation(operation, observedSequenceNumber))
            {
                return SocketError.IOPending;
            }

            bytesSent = operation.BytesTransferred;
            return operation.ErrorCode;
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
                _receiveQueue.HandleEvent(this);
            }

            if ((events & Interop.Sys.SocketEvents.Write) != 0)
            {
                _sendQueue.HandleEvent(this);
            }
        }
    }
}
