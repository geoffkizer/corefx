// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Parser
{
    internal sealed class ConnectionCloseReadStream : HttpContentReadStream
    {
        BufferedStream _bufferedStream;

        public ConnectionCloseReadStream(BufferedStream bufferedStream)
        {
            Debug.Assert(bufferedStream != null);
            _bufferedStream = bufferedStream;
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

            int bytesRead = await _bufferedStream.ReadAsync(buffer, offset, count, cancellationToken);

            if (bytesRead == 0)
            {
                _bufferedStream = null;
                return 0;
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

            await _bufferedStream.CopyToStreamAsync(destination, cancellationToken);

            _bufferedStream = null;
        }

        public override Task DrainAsync(CancellationToken cancellationToken)
        {
            // TODO: How to handle this?
            // Close underlying stream?

            _bufferedStream = null;

            return Task.CompletedTask;
        }
    }
}