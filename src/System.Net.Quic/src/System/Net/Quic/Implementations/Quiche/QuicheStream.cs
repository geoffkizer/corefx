// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Quic.Implementations.Quiche
{
    internal sealed class QuicheStream : QuicStreamProvider
    {
        private bool _disposed = false;
        private readonly long _streamId;
        private bool _canRead;
        private bool _canWrite;

        private QuicheConnection _connection;
        private TaskCompletionSource<bool> _onWriteComplete;
        private TaskCompletionSource<bool> _onReadComplete;
        private bool _receivedFin;

        // Constructor for outbound streams
        internal QuicheStream(QuicheConnection connection, long streamId, bool bidirectional)
        {
            _connection = connection;
            _streamId = streamId;
            _canRead = bidirectional;
            _canWrite = true;
        }

        // Constructor for inbound streams
        internal QuicheStream(Socket socket, long streamId, bool bidirectional)
        {
            _streamId = streamId;
            _canRead = true;
            _canWrite = bidirectional;
        }

        internal override long StreamId
        {
            get
            {
                CheckDisposed();
                return _streamId;
            }
        }

        internal override bool CanRead => _canRead;

        internal override int Read(Span<byte> buffer)
        {
            CheckDisposed();

            if (!_canRead)
            {
                throw new NotSupportedException();
            }

            throw new NotImplementedException();
        }

        internal override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            if (!_canRead)
            {
                throw new NotSupportedException();
            }

            if (_receivedFin)
            {
                return 0;
            }

            while (true)
            {
                (int bytesRead, bool fin) = _connection.StreamReceive(_streamId, buffer);

                if (fin)
                {
                    _receivedFin = true;
                    return bytesRead;
                }

                if (bytesRead > 0)
                {
                    return bytesRead;
                }

                // TODO: Need synchronization here
                // TODO: Really need to move this down into StreamReceive
                Debug.Assert(_onReadComplete == null);
                TaskCompletionSource<bool> onReadComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _onReadComplete = onReadComplete;

                // TODO: Signal this appropriately
                // TODO: Cancellation support here
                await onReadComplete.Task.ConfigureAwait(false);
            }
        }

        internal void OnReadable()
        {
            // Note, this is always called under the connection lock
            if (_onReadComplete != null)
            {
                _onReadComplete.SetResult(true);
                _onReadComplete = null;
            }
        }

        internal override bool CanWrite => _canWrite;

        internal override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckDisposed();

            if (!_canWrite)
            {
                throw new NotSupportedException();
            }

            throw new NotImplementedException();
        }

        internal override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            CheckDisposed();

            if (!_canWrite)
            {
                throw new NotSupportedException();
            }

            int bytesWritten = 0;
            while (true)
            {
                bytesWritten += _connection.StreamSend(_streamId, buffer.Slice(bytesWritten), fin: false);
                Debug.Assert(bytesWritten >= 0 && bytesWritten <= buffer.Length);

                if (bytesWritten == buffer.Length)
                {
                    return;
                }

                // TODO: Need synchronization here
                // TODO: Really need to move this down into StreamReceive
                Debug.Assert(_onWriteComplete == null);
                TaskCompletionSource<bool> onWriteComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _onWriteComplete = onWriteComplete;

                // TODO: Signal this appropriately
                // TODO: Cancellation support here
                await onWriteComplete.Task.ConfigureAwait(false);
            }
        }

        internal void OnWritable()
        {
            // Note, this is always called under the connection lock
            if (_onWriteComplete != null)
            {
                _onWriteComplete.SetResult(true);
                _onWriteComplete = null;
            }
        }

        internal override void Flush()
        {
            CheckDisposed();
        }

        internal override Task FlushAsync(CancellationToken cancellationToken)
        {
            CheckDisposed();

            return Task.CompletedTask;
        }

        internal override void ShutdownRead()
        {
            CheckDisposed();

            throw new NotImplementedException();
        }

        internal override void ShutdownWrite()
        {
            CheckDisposed();

            _connection.StreamSend(_streamId, default, fin: true);
        }

        private void CheckDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(QuicStream));
            }
        }

        public override void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                // TODO
            }
        }
    }
}
