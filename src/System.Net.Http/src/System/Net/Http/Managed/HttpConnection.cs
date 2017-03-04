// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Managed
{
    internal sealed class HttpConnection : IDisposable
    {
        private const int BufferSize = 4096;

        HttpConnectionHandler _handler;
        private HttpConnectionKey _key;
        private TcpClient _client;
        private Stream _stream;
        private TransportContext _transportContext;
        private bool _usingProxy;

        private StringBuilder _sb;

        private byte[] _writeBuffer;
        private int _writeOffset;

        private byte[] _readBuffer;
        private int _readOffset;
        private int _readLength;

        private bool _disposed;

        private sealed class HttpConnectionContent : HttpContent
        {
            private readonly CancellationToken _cancellationToken;
            private Stream _content;
            private bool _contentConsumed;

            public HttpConnectionContent(CancellationToken cancellationToken)
            {
                _cancellationToken = cancellationToken;
            }

            public void SetStream(Stream content)
            {
                Debug.Assert(content != null);
                Debug.Assert(content.CanRead);
                Debug.Assert(!content.CanWrite);
                Debug.Assert(!content.CanSeek);

                _content = content;
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                Debug.Assert(stream != null);

                if (_contentConsumed)
                {
                    throw new InvalidOperationException(SR.net_http_content_stream_already_read);
                }
                _contentConsumed = true;

                const int BufferSize = 8192;
                Task copyTask = _content.CopyToAsync(stream, BufferSize, _cancellationToken);
                if (copyTask.IsCompleted)
                {
                    try { _content.Dispose(); } catch { } // same as StreamToStreamCopy behavior
                }
                else
                {
                    copyTask = copyTask.ContinueWith((t, s) =>
                    {
                        try { ((Stream)s).Dispose(); } catch { }
                        t.GetAwaiter().GetResult();
                    }, _content, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                return copyTask;
            }

            protected internal override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _content.Dispose();
                }
                base.Dispose(disposing);
            }

            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_content);
        }

        private sealed class ContentLengthResponseStream : ReadOnlyStream
        {
            private HttpConnection _connection;
            private long _contentBytesRemaining;

            public ContentLengthResponseStream(HttpConnection connection, long contentLength)
            {
                _connection = connection;
                _contentBytesRemaining = contentLength;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_connection == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                if (_contentBytesRemaining == 0)
                {
                    // End of response body
                    _connection.ResponseBodyCompleted();
                    _connection = null;
                    return 0;
                }

                count = (int)Math.Min(count, _contentBytesRemaining);

                int bytesRead = await _connection.ReadAsync(buffer, offset, count, cancellationToken);

                if (bytesRead == 0)
                {
                    // Unexpected end of response stream
                    throw new IOException("Unexpected end of content stream");
                }

                Debug.Assert(bytesRead <= _contentBytesRemaining);
                _contentBytesRemaining -= bytesRead;

                return bytesRead;
            }
        }

        private sealed class ChunkedEncodingResponseStream : ReadOnlyStream
        {
            private HttpConnection _connection;
            private int _chunkBytesRemaining;

            public ChunkedEncodingResponseStream(HttpConnection connection)
            {
                _connection = connection;
                _chunkBytesRemaining = 0;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_connection == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                if (_chunkBytesRemaining == 0)
                {
                    // Start of chunk, read chunk size
                    int chunkSize = 0;
                    byte b = await _connection.ReadByteAsync(cancellationToken);

                    while (true)
                    {
                        // Get hex digit
                        if (b >= (byte)'0' && b <= (byte)'9')
                        {
                            chunkSize = chunkSize * 16 + (b - (byte)'0');
                        }
                        else if (b >= (byte)'a' && b <= (byte)'f')
                        {
                            chunkSize = chunkSize * 16 + (b - (byte)'a' + 10);
                        }
                        else if (b >= (byte)'A' && b <= (byte)'F')
                        {
                            chunkSize = chunkSize * 16 + (b - (byte)'A' + 10);
                        }
                        else
                        {
                            throw new IOException("Invalid chunk size in response stream");
                        }

                        b = await _connection.ReadByteAsync(cancellationToken);
                        if (b == (byte)'\r')
                        {
                            if (await _connection.ReadByteAsync(cancellationToken) != (byte)'\n')
                                throw new IOException("Saw CR without LF while parsing chunk size");

                            break;
                        }
                    }

                    if (chunkSize == 0)
                    {
                        // Indicates end of response body

                        // We expect final CRLF after this
                        if (await _connection.ReadByteAsync(cancellationToken) != (byte)'\r' ||
                            await _connection.ReadByteAsync(cancellationToken) != (byte)'\n')
                        {
                            throw new IOException("missing final CRLF for chunked encoding");
                        }

                        _connection.ResponseBodyCompleted();
                        _connection = null;
                        return 0;
                    }

                    _chunkBytesRemaining = chunkSize;
                }

                if (count > _chunkBytesRemaining)
                {
                    count = _chunkBytesRemaining;
                }

                int bytesRead = await _connection.ReadAsync(buffer, offset, count, cancellationToken);

                if (bytesRead == 0)
                {
                    // Unexpected end of response stream
                    throw new IOException("Unexpected end of content stream while processing chunked response body");
                }

                Debug.Assert(bytesRead <= _chunkBytesRemaining);
                _chunkBytesRemaining -= bytesRead;

                if (_chunkBytesRemaining == 0)
                {
                    // Parse CRLF at end of chunk
                    if (await _connection.ReadByteAsync(cancellationToken) != (byte)'\r' ||
                        await _connection.ReadByteAsync(cancellationToken) != (byte)'\n')
                    {
                        throw new IOException("missing CRLF for end of chunk");
                    }
                }

                return bytesRead;
            }
        }

        private sealed class ConnectionCloseResponseStream : ReadOnlyStream
        {
            private HttpConnection _connection;

            public ConnectionCloseResponseStream(HttpConnection connection)
            {
                _connection = connection;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_connection == null)
                {
                    // Response body fully consumed
                    return 0;
                }

                int bytesRead = await _connection.ReadAsync(buffer, offset, count, cancellationToken);

                if (bytesRead == 0)
                {
                    // We cannot reuse this connection, so close it.
                    _connection.Dispose();
                    _connection = null;
                    return 0;
                }

                return bytesRead;
            }
        }

        private sealed class ChunkedEncodingRequestStream : WriteOnlyStream
        {
            private HttpConnection _connection;
            private byte[] _chunkLengthBuffer;

            static readonly byte[] _CRLF = new byte[] { (byte)'\r', (byte)'\n' };

            public ChunkedEncodingRequestStream(HttpConnection connection)
            {
                _connection = connection;

                _chunkLengthBuffer = new byte[sizeof(int)];
            }

            // TODO: Need to consider how buffering works between here and the Connection proper...

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // Don't write if nothing was given
                // Especially since we don't want to accidentally send a 0 chunk, which would indicate end of body
                if (count == 0)
                {
                    return;
                }

                // Construct chunk length
                int digit = (sizeof(int));
                int remaining = count;
                while (remaining != 0)
                {
                    digit--;
                    int i = remaining % 16;
                    _chunkLengthBuffer[digit] = (byte)(i < 10 ? '0' + i : 'A' + i - 10);
                    remaining = remaining / 16;
                }

                // Write chunk length
                await _connection.WriteAsync(_chunkLengthBuffer, digit, (sizeof(int)) - digit, cancellationToken);
                await _connection.WriteAsync(_CRLF, 0, _CRLF.Length, cancellationToken);    // TODO: Just use WriteByteAsync here?

                // Write chunk contents
                await _connection.WriteAsync(buffer, offset, count, cancellationToken);
                await _connection.WriteAsync(_CRLF, 0, _CRLF.Length, cancellationToken);
            }

            public async Task CompleteAsync(CancellationToken cancellationToken)
            {
                // Send 0 byte chunk to indicate end
                _chunkLengthBuffer[0] = (byte)'0';
                await _connection.WriteAsync(_chunkLengthBuffer, 0, 1, cancellationToken);
                await _connection.WriteAsync(_CRLF, 0, _CRLF.Length, cancellationToken);

                // Send final _CRLF
                await _connection.WriteAsync(_CRLF, 0, _CRLF.Length, cancellationToken);

                // Flush underlying connection buffer
                await _connection.FlushAsync(cancellationToken);

                _connection = null;
            }
        }

        public HttpConnection(
            HttpConnectionHandler handler, 
            HttpConnectionKey key, 
            TcpClient client, 
            Stream stream, 
            TransportContext transportContext, 
            bool usingProxy)
        {
            _handler = handler;
            _key = key;
            _client = client;
            _stream = stream;
            _transportContext = transportContext;
            _usingProxy = usingProxy;

            _sb = new StringBuilder();

            _writeBuffer = new byte[BufferSize];
            _writeOffset = 0;

            _readBuffer = new byte[BufferSize];
            _readLength = 0;
            _readOffset = 0;
        }

        public HttpConnectionKey Key
        {
            get { return _key; }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                _stream.Dispose();
                _client.Dispose();
            }
        }

        private async Task<HttpResponseMessage> ParseResponse(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new HttpResponseMessage();
            response.RequestMessage = request;

            if (await ReadByteAsync(cancellationToken) != (byte)'H' ||
                await ReadByteAsync(cancellationToken) != (byte)'T' ||
                await ReadByteAsync(cancellationToken) != (byte)'T' ||
                await ReadByteAsync(cancellationToken) != (byte)'P' ||
                await ReadByteAsync(cancellationToken) != (byte)'/' ||
                await ReadByteAsync(cancellationToken) != (byte)'1' ||
                await ReadByteAsync(cancellationToken) != (byte)'.' ||
                await ReadByteAsync(cancellationToken) != (byte)'1')
            {
                throw new HttpRequestException("could not read response HTTP version");
            }

            byte b = await ReadByteAsync(cancellationToken);
            if (b != (byte)' ')
            {
                throw new HttpRequestException("Invalid characters in response");
            }

            byte status1 = await ReadByteAsync(cancellationToken);
            byte status2 = await ReadByteAsync(cancellationToken);
            byte status3 = await ReadByteAsync(cancellationToken);

            if (!char.IsDigit((char)status1) ||
                !char.IsDigit((char)status2) ||
                !char.IsDigit((char)status3))
            {
                throw new HttpRequestException("could not read response status code");
            }

            int status = 100 * (status1 - (byte)'0') + 10 * (status2 - (byte)'0') + (status3 - (byte)'0');
            response.StatusCode = (HttpStatusCode)status;

            b = await ReadByteAsync(cancellationToken);
            if (b != (byte)' ')
            {
                throw new HttpRequestException("Invalid characters in response line");
            }

            _sb.Clear();

            // Parse reason phrase
            b = await ReadByteAsync(cancellationToken);
            while (b != (byte)'\r')
            {
                _sb.Append((char)b);
                b = await ReadByteAsync(cancellationToken);
            }

            b = await ReadByteAsync(cancellationToken);
            if (b != (byte)'\n')
            {
                throw new HttpRequestException("Saw CR without LF while parsing response line");
            }

            response.ReasonPhrase = _sb.ToString();

            var responseContent = new HttpConnectionContent(CancellationToken.None);

            // Parse headers
            _sb.Clear();
            b = await ReadByteAsync(cancellationToken);
            while (true)
            {
                if (b == (byte)'\r')
                {
                    if (await ReadByteAsync(cancellationToken) != (byte)'\n')
                        throw new HttpRequestException("Saw CR without LF while parsing headers");

                    break;
                }

                // Get header name
                while (b != (byte)':')
                {
                    _sb.Append((char)b);
                    b = await ReadByteAsync(cancellationToken);
                }

                string headerName = _sb.ToString();

                _sb.Clear();

                // Get header value
                b = await ReadByteAsync(cancellationToken);
                while (b == (byte)' ')
                {
                    b = await ReadByteAsync(cancellationToken);
                }

                while (b != (byte)'\r')
                {
                    _sb.Append((char)b);
                    b = await ReadByteAsync(cancellationToken);
                }

                if (await ReadByteAsync(cancellationToken) != (byte)'\n')
                    throw new HttpRequestException("Saw CR without LF while parsing headers");

                string headerValue = _sb.ToString();

                // TryAddWithoutValidation will fail if the header name has trailing whitespace.
                // So, trim it here.
                // TODO: Not clear to me from the RFC that this is really correct; RFC seems to indicate this should be an error.
                headerName = headerName.TrimEnd();

                // Add header to appropriate collection
                // Don't ask me why this is the right API to call, but apparently it is
                if (!response.Headers.TryAddWithoutValidation(headerName, headerValue))
                {
                    if (!responseContent.Headers.TryAddWithoutValidation(headerName, headerValue))
                    {
                        // TODO: Not sure why this would happen.  Invalid chars in header name?
                        throw new Exception($"could not add response header, {headerName}: {headerValue}");
                    }
                }

                _sb.Clear();

                b = await ReadByteAsync(cancellationToken);
            }

            // Instantiate responseStream
            Stream responseStream;

            if (request.Method == HttpMethod.Head ||
                status == 204 ||
                status == 304)
            {
                // There is implicitly no response body
                // TODO: Should this mean there's no responseContent at all?
                responseStream = new ContentLengthResponseStream(this, 0);
            }
            else if (responseContent.Headers.ContentLength != null)
            {
                responseStream = new ContentLengthResponseStream(this, responseContent.Headers.ContentLength.Value);
            }
            else if (response.Headers.TransferEncodingChunked == true)
            {
                responseStream = new ChunkedEncodingResponseStream(this);
            }
            else
            {
                responseStream = new ConnectionCloseResponseStream(this);
            }

            responseContent.SetStream(responseStream);
            response.Content = responseContent;
            return response;
        }

        private async Task WriteHeadersAsync(HttpHeaders headers, CancellationToken cancellationToken)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
            {
                await WriteStringAsync(header.Key, cancellationToken);
                await WriteCharAsync(':', cancellationToken);
                await WriteCharAsync(' ', cancellationToken);

                bool first = true;
                foreach (string headerValue in header.Value)
                {
                    if (first)
                    {
                        first = false;
                    }
                    else
                    {
                        await WriteCharAsync(',', cancellationToken);
                        await WriteCharAsync(' ', cancellationToken);
                    }
                    await WriteStringAsync(headerValue, cancellationToken);
                }

                Debug.Assert(!first, "No values for header??");

                await WriteCharAsync('\r', cancellationToken);
                await WriteCharAsync('\n', cancellationToken);
            }
        }

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpContent requestContent = request.Content;

            // Determine if we need transfer encoding
            bool transferEncoding = false;
            long contentLength = 0;
            if (requestContent != null)
            {
                if (request.Headers.TransferEncodingChunked == true)
                {
                    transferEncoding = true;
                }
                else if (requestContent.Headers.ContentLength != null)
                {
                    contentLength = requestContent.Headers.ContentLength.Value;
                }
                else
                {
                    // TODO: Is this the correct behavior?
                    // Seems better to do TryComputeLength on the content first
                    request.Headers.TransferEncodingChunked = true;
                    transferEncoding = true;
                }
            }

            // Add Host header, if not present
            if (request.Headers.Host == null)
            {
                Uri uri = request.RequestUri;
                string hostString = uri.Host;
                if (!uri.IsDefaultPort)
                {
                    hostString += ":" + uri.Port.ToString();
                }

                request.Headers.Host = hostString;
            }

            // Start sending the request
            // TODO: can certainly make this more efficient...

            // Write request line
            await WriteStringAsync(request.Method.Method, cancellationToken);
            await WriteCharAsync(' ', cancellationToken);

            if (_usingProxy)
            {
                await WriteStringAsync(request.RequestUri.AbsoluteUri, cancellationToken);
            }
            else
            {
                await WriteStringAsync(request.RequestUri.PathAndQuery, cancellationToken);
            }

            await WriteCharAsync(' ', cancellationToken);
            await WriteCharAsync('H', cancellationToken);
            await WriteCharAsync('T', cancellationToken);
            await WriteCharAsync('T', cancellationToken);
            await WriteCharAsync('P', cancellationToken);
            await WriteCharAsync('/', cancellationToken);
            await WriteCharAsync('1', cancellationToken);
            await WriteCharAsync('.', cancellationToken);
            await WriteCharAsync('1', cancellationToken);
            await WriteCharAsync('\r', cancellationToken);
            await WriteCharAsync('\n', cancellationToken);

            // Write headers
            await WriteHeadersAsync(request.Headers, cancellationToken);

            if (requestContent != null)
            {
                await WriteHeadersAsync(requestContent.Headers, cancellationToken);
            }

            // Also write out a Content-Length: 0 header if no request body, and this is a method that can have one.
            // TODO: revisit this; should set this as a header and write out as always
            // Actually, under what circumstances can requestContent be null?  Consider...
            if (requestContent == null)
            {
                if (request.Method != HttpMethod.Get &&
                    request.Method != HttpMethod.Head)
                {
                    await WriteStringAsync("Content-Length: 0\r\n", cancellationToken);
                }
            }

            // CRLF for end of headers.
            await WriteCharAsync('\r', cancellationToken);
            await WriteCharAsync('\n', cancellationToken);

            // Flush headers.  We need to do this if we have no body anyway.
            // TODO: Don't think we should always do this, consider...
            await FlushAsync(cancellationToken);

            // Write body, if any
            if (requestContent != null)
            {
                // TODO: 100-continue?

                if (transferEncoding)
                {
                    var stream = new ChunkedEncodingRequestStream(this);

                    // TODO: CopyToAsync doesn't take a CancellationToken, how do we deal with Cancellation here? (and below)
                    await request.Content.CopyToAsync(stream, _transportContext);
                    await stream.CompleteAsync(cancellationToken);
                }
                else
                {
                    // TODO: Is it fine to just pass the underlying stream?
                    // For small content, seems like this should go through buffering
                    await request.Content.CopyToAsync(_stream, _transportContext);
                }
            }

            return await ParseResponse(request, cancellationToken);
        }

        private async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int remaining = BufferSize - _writeOffset;

            if (count <= remaining)
            {
                // Fits in current write buffer.  Just copy and return.
                Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeOffset, count);
                _writeOffset += count;
                return;
            }

            // Fit what we can in the current write buffer and flush it.
            Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeOffset, remaining);

            await FlushAsync(cancellationToken);

            // Update offset and count to reflect the write we just did.
            offset += remaining;
            count -= remaining;

            if (count >= BufferSize)
            {
                // Large write.  No sense buffering this.  Write directly to stream.
                // CONSIDER: May want to be a bit smarter here?  Think about how large writes should work...
                await _stream.WriteAsync(buffer, offset, count, cancellationToken);
            }
            else
            {
                // Copy remainder into buffer
                Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeOffset, 0);
                _writeOffset += count;
            }
        }

        private ValueTask<bool> WriteCharAsync(char c, CancellationToken cancellationToken)
        {
            if ((c & 0xFF80) != 0)
            {
                throw new HttpRequestException("Non-ASCII characters found");
            }

            return WriteByteAsync((byte)c, cancellationToken);
        }

        private ValueTask<bool> WriteByteAsync(byte b, CancellationToken cancellationToken)
        {
            if (_writeOffset < BufferSize)
            {
                _writeBuffer[_writeOffset++] = b;
                return new ValueTask<bool>(true);
            }

            return new ValueTask<bool>(WriteByteSlowAsync(b, cancellationToken));
        }

        private async Task<bool> WriteByteSlowAsync(byte b, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(_writeBuffer, 0, BufferSize, cancellationToken);

            _writeBuffer[0] = b;
            _writeOffset = 1;

            return true;
        }

        // TODO: Consider using ValueTask here
        private async Task WriteStringAsync(string s, CancellationToken cancellationToken)
        {
            for (int i = 0; i < s.Length; i++)
            {
                await WriteCharAsync(s[i], cancellationToken);
            }
        }

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(_writeBuffer, 0, _writeOffset, cancellationToken);
            _writeOffset = 0;
        }

        private async Task FillAsync(CancellationToken cancellationToken)
        {
            Debug.Assert(_readOffset == _readLength);

            _readOffset = 0;
            _readLength = await _stream.ReadAsync(_readBuffer, 0, BufferSize, cancellationToken);
        }

        private async Task<byte> ReadByteSlowAsync(CancellationToken cancellationToken)
        {
            await FillAsync(cancellationToken);

            if (_readLength == 0)
            {
                // End of stream
                throw new IOException("unexpected end of stream");
            }

            return _readBuffer[_readOffset++];
        }

        // TODO: Revisit perf characteristics of this approach
        private ValueTask<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_readOffset < _readLength)
            {
                return new ValueTask<byte>(_readBuffer[_readOffset++]);
            }

            return new ValueTask<byte>(ReadByteSlowAsync(cancellationToken));
        }

        private async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // This is called when reading the response body

            int remaining = _readLength - _readOffset;
            if (remaining > 0)
            {
                // We have data in the read buffer.  Return it to the caller.
                count = Math.Min(count, remaining);
                Buffer.BlockCopy(_readBuffer, _readOffset, buffer, offset, count);

                _readOffset += count;
                Debug.Assert(_readOffset <= _readLength);
                return count;
            }

            // No data in read buffer. 
            if (count < BufferSize / 2)
            {
                // Caller requested a small read size (less than half the read buffer size).
                // Read into the buffer, so that we read as much as possible, hopefully.
                await FillAsync(cancellationToken);

                count = Math.Min(count, _readLength);
                Buffer.BlockCopy(_readBuffer, _readOffset, buffer, offset, count);

                _readOffset += count;
                Debug.Assert(_readOffset <= _readLength);
                return count;
            }

            // Large read size, and no buffered data.
            // Do an unbuffered read directly against the underlying stream.
            count = await _stream.ReadAsync(buffer, offset, count, cancellationToken);
            return count;
        }

        private void ResponseBodyCompleted()
        {
            // Called by the response stream when the body has been fully read

            // Put the connection back in the connection pool, for reuse
            _handler.ReturnConnectionToPool(this);
        }
    }
}
