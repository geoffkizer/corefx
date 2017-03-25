// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Parser
{
    internal sealed class EmptyReadStream : HttpContentReadStream
    {
        private EmptyReadStream()
        {
        }

        public static readonly EmptyReadStream Instance = new EmptyReadStream();

        public override bool CanRead => false;

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
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

            return Task.FromResult(0);
        }

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override Task DrainAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}