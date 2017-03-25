// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Parser
{
    // TODO: need something like Task ReadComplete
    // TODO: complete the task on Dispose
    // TODO: Drain on Dispose as well
    // TODO: EmptyStream, single instance, task already completed
    internal abstract class HttpContentReadStream : Stream
    {
        protected TaskCompletionSource<bool> _tcs;

        protected HttpContentReadStream()
        {
            _tcs = new TaskCompletionSource<bool>();
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length
        {
            get { throw new NotSupportedException(); }
        }

        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).Result;
        }

        public override void CopyTo(Stream destination, int bufferSize)
        {
            CopyToAsync(destination, bufferSize, CancellationToken.None).Wait();
        }

        public abstract override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
        public abstract override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken);

        public Task WaitForCompletionAsync()
        {
            return _tcs.Task;
        }

        protected override void Dispose(bool disposing)
        {
            // TODO: Drain

            base.Dispose(disposing);
        }
    }
}
