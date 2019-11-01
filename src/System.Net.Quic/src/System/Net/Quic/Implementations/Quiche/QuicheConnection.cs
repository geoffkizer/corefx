// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Buffers;

namespace System.Net.Quic.Implementations.Quiche
{
    internal sealed class QuicheConnection : QuicConnectionProvider
    {
        private readonly bool _isClient;
        private readonly SslClientAuthenticationOptions _sslClientAuthenticationOptions;
        private bool _disposed = false;
        private IPEndPoint _remoteEndPoint;
        private IPEndPoint _localEndPoint;
        private object _syncObject = new object();

        private Socket _socket = null;
        private IntPtr _connectionHandle;
        private TaskCompletionSource<bool> _signalSend;
        private SslApplicationProtocol _negotiatedApplicationProtocol;

        // TODO: This should be a dictionary holding all streams
        private QuicheStream _activeStream;

        // TODO: Goes away
        private IPEndPoint _peerListenEndPoint = null;
        private TcpListener _inboundListener = null;

        private long _nextOutboundBidirectionalStream;
        private long _nextOutboundUnidirectionalStream;

        // Constructor for outbound connections
        internal QuicheConnection(IPEndPoint remoteEndPoint, SslClientAuthenticationOptions sslClientAuthenticationOptions, IPEndPoint localEndPoint = null)
        {
            _remoteEndPoint = remoteEndPoint;
            _localEndPoint = localEndPoint;
            _sslClientAuthenticationOptions = sslClientAuthenticationOptions;

            _isClient = true;
            _nextOutboundBidirectionalStream = 4;
            _nextOutboundUnidirectionalStream = 2;
        }

        // Constructor for accepted inbound connections
        internal QuicheConnection(Socket socket, IPEndPoint peerListenEndPoint, TcpListener inboundListener)
        {
            _isClient = false;
            _nextOutboundBidirectionalStream = 1;
            _nextOutboundUnidirectionalStream = 3;
            _socket = socket;
            _peerListenEndPoint = peerListenEndPoint;
            _inboundListener = inboundListener;
            _localEndPoint = (IPEndPoint)socket.LocalEndPoint;
            _remoteEndPoint = (IPEndPoint)socket.RemoteEndPoint;
        }

        internal override bool Connected
        {
            get
            {
                CheckDisposed();

                return _socket != null;
            }
        }

        internal override IPEndPoint LocalEndPoint => new IPEndPoint(_localEndPoint.Address, _localEndPoint.Port);

        internal override IPEndPoint RemoteEndPoint => new IPEndPoint(_remoteEndPoint.Address, _remoteEndPoint.Port);

        internal override SslApplicationProtocol NegotiatedApplicationProtocol
        {
            get
            {
                CheckDisposed();

                if (!Connected)
                {
                    throw new InvalidOperationException();
                }

                return _negotiatedApplicationProtocol;
            }
        }

        internal override async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            if (Connected)
            {
                // TODO: Exception text
                throw new InvalidOperationException("Already connected");
            }

            // Create the UDP socket
            Socket socket = new Socket(_remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(_remoteEndPoint);
            _socket = socket;
            _localEndPoint = (IPEndPoint)socket.LocalEndPoint;
            Log($"Socket connected from {_localEndPoint} to {_remoteEndPoint}");

            CreateNativeConnection();

            TaskCompletionSource<bool> onConnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task connectTask = onConnect.Task;

            // Start receive and send tasks
            _ = ProcessOutgoingAsync();
            _ = ProcessIncomingAsync(onConnect);

            // Wait for connect to complete
            await connectTask.ConfigureAwait(false);
        }

        private unsafe void CreateNativeConnection()
        {
            // Create a connection ID.
            // For now, we are not storing this ourselves (Quiche will store it)
            QuicheConnectionId connId = QuicheConnectionId.NewId();

            // Create a Quiche config object with appropriate settings
            IntPtr configHandle = QuicheConfigFactory.CreateConfig(_sslClientAuthenticationOptions.ApplicationProtocols);

            fixed (byte* scidPtr = connId.Value.Span)
            {
                _connectionHandle = NativeMethods.quiche_connect(
                    _sslClientAuthenticationOptions.TargetHost,
                    scidPtr,
                    (UIntPtr)connId.Value.Length,
                    configHandle);
            }

            Debug.Assert(_connectionHandle != IntPtr.Zero);

            NativeMethods.quiche_config_free(configHandle);

            Log($"Created native connection {_connectionHandle}");
        }

        private unsafe (int, Task) GetOutgoingDatagram(byte[] buffer)
        {
            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                Debug.Assert(_signalSend == null);

                if (_connectionHandle == IntPtr.Zero)
                {
                    // Connection has been closed. No data to send.
                    return (0, null);
                }

                // Pin the buffer and ask Quiche to fill it.
                int result;
                fixed (byte* ptr = buffer)
                {
                    result = (int)NativeMethods.quiche_conn_send(_connectionHandle, ptr, (UIntPtr)buffer.Length);
                }

                Debug.Assert(result != 0);
                if (result == -1)
                {
                    // TODO: Need some logic here around timeout
                    // No data to send.

                    // Create the signal task to wake us up for subsequent sends.
                    _signalSend = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    return (0, _signalSend.Task);
                }
                else if (result < 0)
                {
                    // Error.
                    // TODO: Better exception/error handling.
                    throw new Exception($"Quiche error on call to quiche_conn_send: {result}");
                }

                Debug.Assert(result < NativeMethods.QUICHE_MAX_DATAGRAM_SIZE);
                return (result, null);
            }
        }

        // TODO: Timeout/cancellation
        private async Task ProcessOutgoingAsync()
        {
            try
            {
                byte[] buffer = new byte[NativeMethods.QUICHE_MAX_DATAGRAM_SIZE];

                while (true)
                {
                    Task signalTask;
                    while (true)
                    {
                        int datagramSize;
                        (datagramSize, signalTask) = GetOutgoingDatagram(buffer);
                        if (datagramSize == 0)
                        {
                            Debug.Assert(signalTask != null);
                            break;
                        }

                        // Send the datagram
                        Log($"Sending datagram of {datagramSize} bytes.");
                        int bytesSent = await _socket.SendAsync(buffer.AsMemory().Slice(0, datagramSize), SocketFlags.None).ConfigureAwait(false);
                        Debug.Assert(bytesSent == datagramSize);
                    }

                    Log("Finished sending.");

                    // Wait for either the signal that we have more to send, or the timeout
                    await signalTask.ConfigureAwait(false);

                    Log("Send task signalled");
                }
            }
            catch (Exception e)
            {
                Log($"Caught exception in ProcessOutgoingAsync: {e}");
                // TODO
            }
        }

        // TODO: This probably shouldn't be top-level
        // It should happen under the lock in appropriate places
        private void SignalSend()
        {
            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                if (_signalSend != null)
                {
                    _signalSend.SetResult(true);
                    _signalSend = null;
                }
            }
        }

        private unsafe void ProcessIncomingDatagram(Memory<byte> buffer, ref TaskCompletionSource<bool> onConnect)
        {
            Debug.Assert(buffer.Length > 0);

            Log($"Giving Quiche {buffer.Length} bytes.");

            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                int result;
                fixed (byte* pinned = buffer.Span)
                {
                    result = (int)NativeMethods.quiche_conn_recv(_connectionHandle, pinned, (UIntPtr)buffer.Length);
                }

                QuicheException.ThrowOnError(result);

                if (result == -1)
                {
                    Log($"Quiche quiche_conn_recv returned Done.");
                }
                else
                {
                    Log($"Quiche quiche_conn_recv processed {result} out of {buffer.Length} received bytes.");

                    // TODO: Can we ever get less bytes processed than we handed to Quiche?
                    Debug.Assert(result == buffer.Length);
                }

                // See if the connection has become established
                if (onConnect != null && NativeMethods.quiche_conn_is_established(_connectionHandle))
                {
                    Log("Connection established.");

                    // Read the Application Protocol
                    NativeMethods.quiche_conn_application_proto(_connectionHandle, out IntPtr appProtoPtr, out UIntPtr appProtoLen);
                    byte[] appProtocolBuffer = new byte[(int)appProtoLen];
                    Marshal.Copy(appProtoPtr, appProtocolBuffer, 0, (int)appProtoLen);
                    _negotiatedApplicationProtocol = new SslApplicationProtocol(appProtocolBuffer);

                    onConnect.SetResult(true);
                    onConnect = null;
                }

                // See if connection is now closed
                if (NativeMethods.quiche_conn_is_closed(_connectionHandle))
                {
                    Log("Connection closed.");
                    // TODO: Handle connection close
                }

                // Check for streams that have become readable
                Log($"Checking for readable streams");
                IntPtr readableHandle = NativeMethods.quiche_conn_readable(_connectionHandle);
                while (NativeMethods.quiche_stream_iter_next(readableHandle, out ulong streamId))
                {
                    Log($"Stream {streamId} is readable");

                    // Look up the stream
                    // TODO: stream dictionary
                    if (_activeStream != null && _activeStream.StreamId == (long)streamId)
                    {
                        _activeStream.OnReadable();
                    }
                }

                NativeMethods.quiche_stream_iter_free(readableHandle);

                // Check for streams that have become writable
                Log($"Checking for writable streams");
                IntPtr writableHandle = NativeMethods.quiche_conn_writable(_connectionHandle);
                while (NativeMethods.quiche_stream_iter_next(writableHandle, out ulong streamId))
                {
                    Log($"Stream {streamId} is writable");

                    // Look up the stream
                    // TODO: stream dictionary
                    if (_activeStream != null && _activeStream.StreamId == (long)streamId)
                    {
                        _activeStream.OnWritable();
                    }
                }

                NativeMethods.quiche_stream_iter_free(writableHandle);

                // Hack test
                if (_activeStream != null)
                {
                    _activeStream.OnReadable();
                }
            }
        }

        private async Task ProcessIncomingAsync(TaskCompletionSource<bool> onConnect)
        {
            try
            {
                byte[] buffer = new byte[NativeMethods.QUICHE_MAX_DATAGRAM_SIZE];

                // Keep looping until we're told we're closed
                // If we're not closed, we expect at least one more UDP datagram, so we keep looping.
                //while (!_closedTcs.Task.IsCompleted)
                while (true)
                {
                    // Recieve from the socket
                    Log("Waiting for incoming datagram");

                    int bytesReceived = await _socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None);

                    Log($"Received incoming datagram of {bytesReceived} bytes.");

                    Debug.Assert(bytesReceived > 0);
                    Debug.Assert(bytesReceived <= NativeMethods.QUICHE_MAX_DATAGRAM_SIZE);

                    // Hand it to quiche
                    ProcessIncomingDatagram(buffer.AsMemory().Slice(0, bytesReceived), ref onConnect);

                    // Signal we may have more data to send
                    // TODO: Move this under lock in ProcessIncomingDatagram
                    // TODO: Can we reliably determine if we actually have data to send? Otherwise we're always signalling
                    // the send task here, which may not be ideal.
                    SignalSend();
                }

                // TODO: Need to terminate loop when connection is closed
                // Presumably we can determine this two ways:
                // (1) Detect that the connection is closed after a receive
                // (2) Socket is closed, and ReceiveAsync throws
                //          Log("Ending incoming loop");
            }
            catch (Exception e)
            {
                Log($"Caught exception in ProcessIncomingAsync: {e}");
                // TODO
            }
        }

        // Temporary
        private static void Log(string s)
        {
            Console.WriteLine(s);
        }

        internal override QuicStreamProvider OpenUnidirectionalStream()
        {
            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                long streamId = _nextOutboundUnidirectionalStream;
                _nextOutboundUnidirectionalStream += 4;

                QuicheStream stream = new QuicheStream(this, streamId, bidirectional: false);
                _activeStream = stream;
                return stream;
            }
        }

        internal override QuicStreamProvider OpenBidirectionalStream()
        {
            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                long streamId = _nextOutboundBidirectionalStream;
                _nextOutboundBidirectionalStream += 4;

                QuicheStream stream = new QuicheStream(this, streamId, bidirectional: true);
                _activeStream = stream;
                return stream;
            }
        }

        internal unsafe int StreamSend(long streamId, ReadOnlyMemory<byte> buffer, bool fin)
        {
            Debug.Assert(buffer.Length > 0 || fin, "StreamSend called with no data and no fin");

            Log($"Calling quiche_conn_stream_send with {buffer.Length} bytes, fin={fin}");

            int result;
            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                fixed (byte* pinned = buffer.Span)
                {
                    result = (int)NativeMethods.quiche_conn_stream_send(_connectionHandle, (ulong)streamId, pinned, (UIntPtr)buffer.Length, fin);
                }
            }

            QuicheException.ThrowOnError(result);

            if (result == (int)QuicheErrorCode.Done)
            {
                // Nothing written.
                Log($"quiche_conn_stream_send returned Done");
                Debug.Assert(buffer.Length > 0);
                return 0;
            }

            Log($"quiche_conn_stream_send accepted {result} bytes");
            Debug.Assert(result <= buffer.Length);

            // Signal the send loop that there's data to flush
            SignalSend();

            // Return # of bytes actually written
            return result;
        }

        internal unsafe (int bytesReceived, bool fin) StreamReceive(long streamId, Memory<byte> buffer)
        {
            Debug.Assert(buffer.Length > 0, "StreamReceive called with empty buffer");

            int result;
            bool fin;
            Debug.Assert(!Monitor.IsEntered(_syncObject));
            lock (_syncObject)
            {
                fixed (byte* pinned = buffer.Span)
                {
                    result = (int)NativeMethods.quiche_conn_stream_recv(_connectionHandle, (ulong)streamId, pinned, (UIntPtr)buffer.Length, out fin);
                }
            }

            QuicheException.ThrowOnError(result);

            if (result == (int)QuicheErrorCode.Done)
            {
                // Nothing written.
                Log($"quiche_conn_stream_recv on {streamId} returned Done; fin={fin}");
                return (0, fin);
            }

            Log($"quiche_conn_stream_recv on {streamId} returned {result} bytes, fin={fin}");
            Debug.Assert(result > 0 && result <= buffer.Length);

            // TODO: Do we need to signal send here? Seems like we could need to update the window, for example
            SignalSend();

            return (result, fin);
        }

        internal override async ValueTask<QuicStreamProvider> AcceptStreamAsync(CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            Socket socket = await _inboundListener.AcceptSocketAsync().ConfigureAwait(false);

            // Read first bytes to get stream ID
            byte[] buffer = new byte[8];
            int bytesRead = 0;
            do
            {
                bytesRead += await socket.ReceiveAsync(buffer.AsMemory().Slice(bytesRead), SocketFlags.None).ConfigureAwait(false);
            } while (bytesRead != buffer.Length);

            long streamId = BinaryPrimitives.ReadInt64LittleEndian(buffer);

            bool clientInitiated = ((streamId & 0b01) == 0);
            if (clientInitiated == _isClient)
            {
                throw new Exception($"Wrong initiator on accepted stream??? streamId={streamId}, _isClient={_isClient}");
            }

            bool bidirectional = ((streamId & 0b10) == 0);

            // TODO
            throw new NotImplementedException();
            //return new MockStream(socket, streamId, bidirectional: bidirectional);
        }

        internal override void Close()
        {
            Dispose();
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QuicConnection));
            }
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _socket?.Dispose();
                    _socket = null;

                    _inboundListener?.Stop();
                    _inboundListener = null;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                _disposed = true;
            }
        }

        ~QuicheConnection()
        {
            Dispose(false);
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
