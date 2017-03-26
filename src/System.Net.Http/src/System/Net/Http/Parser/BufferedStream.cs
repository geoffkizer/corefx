// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Parser
{
    // This class provides a buffered stream-like API on top of an underlying stream (e.g. NetworkStream).
    // It provides the following capabilities:
    // (1) Direct access to read/write buffers and current read/write offset within them
    // (2) Buffer-optimized implementations of some standard Stream APIs, e.g. Read/WriteAsync, FlushAsync, CopyToAsync.

    // CONSIDER: Should this actually derive from Stream?  Maybe...

    public sealed class BufferedStream : IDisposable
    {
        // CONSIDER: Make this configurable
        private const int BufferSize = 4096;

        private readonly Stream _stream;

        private readonly byte[] _writeBuffer;
        private int _writeOffset;

        private readonly byte[] _readBuffer;
        private int _readOffset;
        private int _readLength;

        private bool _disposed;

        public BufferedStream(Stream stream)
        {
            _stream = stream;

            _writeBuffer = new byte[BufferSize];
            _writeOffset = 0;

            _readBuffer = new byte[BufferSize];
            _readLength = 0;
            _readOffset = 0;
        }

        public byte[] WriteBuffer => _writeBuffer;
        public byte[] ReadBuffer => _readBuffer;

        public int WriteLength => BufferSize;
        public int ReadLength => _readLength;

        public int WriteOffset
        {
            get => _writeOffset;
            set
            {
                if (value < 0 || value > WriteLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _writeOffset = value;
            }
        }

        public int ReadOffset
        {
            get => _readOffset;
            set
            {
                if (value < 0 || value > ReadLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _readOffset = value;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                _stream.Dispose();
            }
        }

        public SlimTask<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_readOffset < _readLength)
            {
                return new SlimTask<byte>(_readBuffer[_readOffset++]);
            }

            async SlimTask<byte> ReadByteSlowAsync()
            {
                await FillAsync(cancellationToken);
                if (_readLength == 0)
                {
                    throw new IOException("Unexpected end of stream");
                }

                return _readBuffer[_readOffset++];
            }

            return ReadByteSlowAsync();
        }

        // TODO: Add WriteByteAsync

        public void CopyToBuffer(byte[] byteArray, int offset, int count)
        {
            Debug.Assert(count <= BufferSize - _writeOffset);

            Buffer.BlockCopy(byteArray, offset, _writeBuffer, _writeOffset, count);
            _writeOffset += count;
        }

        public async SlimTask WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int remaining = BufferSize - _writeOffset;

            if (count <= remaining)
            {
                // Fits in current write buffer.  Just copy and return.
                CopyToBuffer(buffer, offset, count);
                return;
            }

            if (_writeOffset != 0)
            {
                // Fit what we can in the current write buffer and flush it.
                CopyToBuffer(buffer, offset, remaining);
                await FlushAsync(cancellationToken);

                // Update offset and count to reflect the write we just did.
                offset += remaining;
                count -= remaining;
            }

            if (count >= BufferSize)
            {
                // Large write.  No sense buffering this.  Write directly to stream.
                // CONSIDER: May want to be a bit smarter here?  Think about how large writes should work...
                await _stream.WriteAsync(buffer, offset, count, cancellationToken);
            }
            else
            {
                // Copy remainder into buffer
                CopyToBuffer(buffer, offset, count);
            }
        }

        public async SlimTask FlushAsync(CancellationToken cancellationToken)
        {
            if (_writeOffset > 0)
            {
                await _stream.WriteAsync(_writeBuffer, 0, _writeOffset, cancellationToken);
                _writeOffset = 0;
            }
        }

        public async SlimTask FillAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_readOffset == _readLength);

            _readOffset = 0;
            _readLength = await _stream.ReadAsync(_readBuffer, 0, BufferSize, cancellationToken);
        }

        public void CopyFromBuffer(byte[] byteArray, int offset, int count)
        {
            Debug.Assert(count <= _readLength - _readOffset);

            Buffer.BlockCopy(_readBuffer, _readOffset, byteArray, offset, count);
            _readOffset += count;
        }

        public async SlimTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // This is called when reading the response body

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                count = Math.Min(count, remaining);
                CopyFromBuffer(buffer, offset, count);
                return count;
            }

            // No data in read buffer. 
            if (count < BufferSize / 2)
            {
                // Caller requested a small read size (less than half the read buffer size).
                // Read into the buffer, so that we read as much as possible, hopefully.
                await FillAsync(cancellationToken);

                count = Math.Min(count, _readLength);
                CopyFromBuffer(buffer, offset, count);
                return count;
            }

            // Large read size, and no buffered data.
            // Do an unbuffered read directly against the underlying stream.
            count = await _stream.ReadAsync(buffer, offset, count, cancellationToken);
            return count;
        }

        public async SlimTask WriteBufferToStreamAsync(Stream destination, int count, CancellationToken cancellationToken)
        {
            Debug.Assert(count <= _readLength - _readOffset);

            await destination.WriteAsync(_readBuffer, _readOffset, count, cancellationToken);
            _readOffset += count;
        }

        public async SlimTask CopyToStreamAsync(Stream destination, CancellationToken cancellationToken)
        {
            Debug.Assert(destination != null);

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                await WriteBufferToStreamAsync(destination, remaining, cancellationToken);
            }

            while (true)
            {
                await FillAsync(cancellationToken);
                if (_readLength == 0)
                {
                    // End of stream
                    break;
                }

                await WriteBufferToStreamAsync(destination, _readLength, cancellationToken);
            }
        }

        // Copy *exactly* [length] bytes into destination; throws on end of stream.
        public async SlimTask CopyToStreamAsync(Stream destination, long length, CancellationToken cancellationToken)
        {
            Debug.Assert(destination != null);
            Debug.Assert(length > 0);

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                remaining = (int)Math.Min(remaining, length);
                await WriteBufferToStreamAsync(destination, remaining, cancellationToken);

                length -= remaining;
                if (length == 0)
                {
                    return;
                }
            }

            while (true)
            {
                await FillAsync(cancellationToken);
                if (_readLength == 0)
                {
                    throw new HttpRequestException("unexpected end of stream");
                }

                remaining = (int)Math.Min(_readLength, length);
                await WriteBufferToStreamAsync(destination, remaining, cancellationToken);

                length -= remaining;
                if (length == 0)
                {
                    return;
                }
            }
        }

        public async SlimTask DrainAsync(long drainLength, CancellationToken cancellationToken)
        {
            int remainder = (int)Math.Min(_readLength - _readOffset, drainLength);
            _readOffset += remainder;
            drainLength -= remainder;

            while (drainLength > 0)
            {
                await FillAsync(cancellationToken);
                if (_readLength == 0)
                {
                    // Unexpected end of response stream
                    throw new IOException("Unexpected end of content stream");
                }

                remainder = (int)Math.Min(_readLength, drainLength);
                _readOffset = remainder;
                drainLength -= remainder;
            }
        }

        public bool HasBufferedReadBytes => (_readOffset < _readLength);
    }
}
