// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Parser
{
    internal sealed class ChunkedEncodingReadStream : HttpContentReadStream
    {
        BufferedStream _bufferedStream;
        private int _chunkBytesRemaining;

        public ChunkedEncodingReadStream(BufferedStream bufferedStream)
        {
            Debug.Assert(bufferedStream != null);
            _bufferedStream = bufferedStream;

            _chunkBytesRemaining = 0;
        }

        private async Task<bool> TryGetNextChunk(CancellationToken cancellationToken)
        {
            Debug.Assert(_chunkBytesRemaining == 0);

            // Start of chunk, read chunk size
            int chunkSize = 0;
            byte b = await _bufferedStream.ReadByteAsync(cancellationToken);
            while (true)
            {
                (bool success, int digit) = Utf8Helpers.TryGetHexDigitValue(b);
                if (!success)
                {
                    throw new IOException("Invalid chunk size in response stream");
                }

                chunkSize = chunkSize * 16 + digit;

                b = await _bufferedStream.ReadByteAsync(cancellationToken);
                if (b == Utf8Helpers.UTF8_CR)
                {
                    if (await _bufferedStream.ReadByteAsync(cancellationToken) != Utf8Helpers.UTF8_LF)
                    {
                        throw new IOException("Saw CR without LF while parsing chunk size");
                    }

                    break;
                }
            }

            _chunkBytesRemaining = chunkSize;
            if (chunkSize == 0)
            {
                // Indicates end of response body

                // We expect final CRLF after this
                if (await _bufferedStream.ReadByteAsync(cancellationToken) != Utf8Helpers.UTF8_CR ||
                    await _bufferedStream.ReadByteAsync(cancellationToken) != Utf8Helpers.UTF8_LF)
                {
                    throw new IOException("missing final CRLF for chunked encoding");
                }

                _bufferedStream = null;
                return false;
            }

            return true;
        }

        private async Task ConsumeChunkBytes(int bytesConsumed, CancellationToken cancellationToken)
        {
            Debug.Assert(bytesConsumed <= _chunkBytesRemaining);
            _chunkBytesRemaining -= bytesConsumed;

            if (_chunkBytesRemaining == 0)
            {
                // Parse CRLF at end of chunk
                if (await _bufferedStream.ReadByteAsync(cancellationToken) != Utf8Helpers.UTF8_CR ||
                    await _bufferedStream.ReadByteAsync(cancellationToken) != Utf8Helpers.UTF8_LF)
                {
                    throw new IOException("missing CRLF for end of chunk");
                }
            }
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0 || count > buffer.Length - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (_bufferedStream == null)
            {
                // Response body fully consumed
                return 0;
            }

            if (_chunkBytesRemaining == 0)
            {
                if (!await TryGetNextChunk(cancellationToken))
                {
                    // End of response body
                    return 0;
                }
            }

            count = Math.Min(count, _chunkBytesRemaining);

            int bytesRead = await _bufferedStream.ReadAsync(buffer, offset, count, cancellationToken);

            if (bytesRead == 0)
            {
                // Unexpected end of response stream
                throw new IOException("Unexpected end of content stream while processing chunked response body");
            }

            await ConsumeChunkBytes(bytesRead, cancellationToken);

            return bytesRead;
        }

        public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (_bufferedStream == null)
            {
                // Response body fully consumed
                return;
            }

            if (_chunkBytesRemaining > 0)
            {
                await _bufferedStream.CopyToStreamAsync(destination, _chunkBytesRemaining, cancellationToken);
                await ConsumeChunkBytes(_chunkBytesRemaining, cancellationToken);
            }

            while (await TryGetNextChunk(cancellationToken))
            {
                await _bufferedStream.CopyToStreamAsync(destination, _chunkBytesRemaining, cancellationToken);
                await ConsumeChunkBytes(_chunkBytesRemaining, cancellationToken);
            }
        }
    }
}