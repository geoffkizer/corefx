// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Parser
{
    internal sealed class ContentLengthReadStream : HttpContentReadStream
    {
        private BufferedStream _bufferedStream;
        private long _contentBytesRemaining;

        public ContentLengthReadStream(BufferedStream bufferedStream, long contentLength)
        {
            Debug.Assert(bufferedStream != null);
            _bufferedStream = bufferedStream;

            Debug.Assert(contentLength > 0);
            _contentBytesRemaining = contentLength;
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

            Debug.Assert(_contentBytesRemaining > 0);

            count = (int)Math.Min(count, _contentBytesRemaining);

            int bytesRead = await _bufferedStream.ReadAsync(buffer, offset, count, cancellationToken);

            if (bytesRead == 0)
            {
                // Unexpected end of response stream
                throw new IOException("Unexpected end of content stream");
            }

            Debug.Assert(bytesRead <= _contentBytesRemaining);
            _contentBytesRemaining -= bytesRead;

            if (_contentBytesRemaining == 0)
            {
                // End of response body
                _bufferedStream = null;
            }

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

            await _bufferedStream.CopyToStreamAsync(destination, _contentBytesRemaining, cancellationToken);

            _contentBytesRemaining = 0;
            _bufferedStream = null;
        }

        public async override Task DrainAsync(CancellationToken cancellationToken)
        {
            if (_bufferedStream == null)
            {
                // Response body fully consumed
                return;
            }

            int remainder = (int)Math.Min(_bufferedStream.ReadLength - _bufferedStream.ReadOffset, _contentBytesRemaining);
            _bufferedStream.ReadOffset += remainder;
            _contentBytesRemaining -= remainder;

            while (_contentBytesRemaining > 0)
            {
                await _bufferedStream.FillAsync(cancellationToken);
                if (_bufferedStream.ReadLength == 0)
                {
                    // Unexpected end of response stream
                    throw new IOException("Unexpected end of content stream");
                }

                remainder = (int)Math.Min(_bufferedStream.ReadLength, _contentBytesRemaining);
                _bufferedStream.ReadOffset = remainder;
                _contentBytesRemaining -= remainder;
            }

            _bufferedStream = null;
        }
    }
}