using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;
using System.Net.Http.Headers;

namespace System.Net.Http.Managed
{
    // TODO: Much code possibly shared with client

    public sealed class HttpServer
    {
        private IPEndPoint _ipEndpoint;
        private HttpClientHandler _handler;

        private static bool s_trace = false;

        private static void Trace(string msg)
        {
            if (s_trace)
            {
                Console.WriteLine(msg);
            }
        }

        public HttpServer(IPEndPoint ipEndpoint, HttpClientHandler handler)
        {
            _ipEndpoint = ipEndpoint;
            _handler = handler;
        }

        public async Task Run()
        {
            TcpListener listener = new TcpListener(_ipEndpoint);
            listener.Start();

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();

                HandleConnection(client);
            }
        }

        private void HandleConnection(TcpClient client)
        {
            Task.Run(() =>
            {
                var connection = new HttpServerConnection(this, client);
                connection.Run();
            });
        }

        private sealed class HttpServerConnection
        {
            private const int BufferSize = 4096;

            private HttpServer _server;
            private TcpClient _client;
            private Stream _stream;

            private Encoder _encoder;

            private byte[] _writeBuffer;
            private int _writeOffset;

            private byte[] _readBuffer;
            private int _readOffset;
            private int _readLength;

            public HttpServerConnection(HttpServer server, TcpClient client)
            {
                _server = server;
                _client = client;

                _stream = client.GetStream();

                _encoder = new UTF8Encoding(true).GetEncoder();

                _writeBuffer = new byte[BufferSize];
                _writeOffset = 0;

                _readBuffer = new byte[BufferSize];
                _readLength = 0;
                _readOffset = 0;
            }

            public async void Run()
            {
                HttpRequestMessage message = await ParseRequest();
            }

            private async Task<byte> DoReadAndGetByteAsync()
            {
                _readLength = await _stream.ReadAsync(_readBuffer, 0, BufferSize);
                if (_readLength == 0)
                {
                    // End of stream
                    throw new IOException("unexpected end of stream");
                }

                _readOffset = 1;
                return _readBuffer[0];
            }

            // This is probably terribly inefficient
            private ValueTask<byte> ReadByteAsync()
            {
                if (_readOffset < _readLength)
                {
                    return new ValueTask<byte>(_readBuffer[_readOffset++]);
                }

                return new ValueTask<byte>(DoReadAndGetByteAsync());
            }

            internal async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // This is called when reading the response body

                int remaining = _readLength - _readOffset;
                if (remaining == 0)
                {
                    _readOffset = 0;
                    _readLength = await _stream.ReadAsync(_readBuffer, 0, BufferSize, cancellationToken);
                    if (_readLength == 0)
                    {
                        // End of stream, what do we do here?
                        return 0;
                    }

                    remaining = _readLength;
                }

                count = count > remaining ? remaining : count;
                Buffer.BlockCopy(_readBuffer, _readOffset, buffer, offset, count);

                _readOffset += count;
                Debug.Assert(_readOffset <= _readLength);
                return count;
            }

            private async Task<HttpRequestMessage> ParseRequest()
            {
                HttpRequestMessage request = new HttpRequestMessage();
                byte b;
                StringBuilder sb = new StringBuilder();

                // Read method
                b = await ReadByteAsync();
                if (b == (byte)' ')
                {
                    throw new HttpRequestException("could not read request method");
                }

                do
                {
                    sb.Append((char)b);
                    b = await ReadByteAsync();
                } while (b != (byte)' ');

                request.Method = new HttpMethod(sb.ToString());
                sb.Clear();

                // Read Uri
                b = await ReadByteAsync();
                if (b == (byte)' ')
                {
                    throw new HttpRequestException("could not read request uri");
                }

                do
                {
                    sb.Append((char)b);
                    b = await ReadByteAsync();
                } while (b != (byte)' ');

                request.RequestUri = new Uri(sb.ToString());
                sb.Clear();

                // Read Http version
                if (await ReadByteAsync() != (byte)'H' ||
                    await ReadByteAsync() != (byte)'T' ||
                    await ReadByteAsync() != (byte)'T' ||
                    await ReadByteAsync() != (byte)'P' ||
                    await ReadByteAsync() != (byte)'/' ||
                    await ReadByteAsync() != (byte)'1' ||
                    await ReadByteAsync() != (byte)'.' ||
                    await ReadByteAsync() != (byte)'1')
                {
                    throw new HttpRequestException("could not read response HTTP version");
                }

                if (await ReadByteAsync() != (byte)'\r' ||
                    await ReadByteAsync() != (byte)'\n')
                {
                    throw new HttpRequestException("expected CRLF");
                }

                var requestContent = new NetworkContent(CancellationToken.None);

                // Parse headers
                b = await ReadByteAsync();
                while (true)
                {
                    if (b == (byte)'\r')
                    {
                        if (await ReadByteAsync() != (byte)'\n')
                            throw new HttpRequestException("Saw CR without LF while parsing headers");

                        break;
                    }

                    // Get header name
                    while (b != (byte)':')
                    {
                        sb.Append((char)b);
                        b = await ReadByteAsync();
                    }

                    string headerName = sb.ToString();

                    sb.Clear();

                    // Get header value
                    b = await ReadByteAsync();
                    while (b == (byte)' ')
                    {
                        b = await ReadByteAsync();
                    }

                    while (b != (byte)'\r')
                    {
                        sb.Append((char)b);
                        b = await ReadByteAsync();
                    }

                    if (await ReadByteAsync() != (byte)'\n')
                        throw new HttpRequestException("Saw CR without LF while parsing headers");

                    string headerValue = sb.ToString();

                    // TryAddWithoutValidation will fail if the header name has trailing whitespace.
                    // So, trim it here.
                    // TODO: Not clear to me from the RFC that this is really correct; RFC seems to indicate this should be an error.
                    headerName = headerName.TrimEnd();

                    // Add header to appropriate collection
                    // Don't ask me why this is the right API to call, but apparently it is
                    if (!request.Headers.TryAddWithoutValidation(headerName, headerValue))
                    {
                        if (!requestContent.Headers.TryAddWithoutValidation(headerName, headerValue))
                        {
                            // TODO: Not sure why this would happen.  Invalid chars in header name?
                            throw new Exception($"could not add response header, {headerName}: {headerValue}");
                        }
                    }

                    sb.Clear();

                    b = await ReadByteAsync();
                }

                // Instantiate requestStream
                Stream requestStream;

                // TODO: Other cases where implicit no request body
                if (request.Method == HttpMethod.Get)
                {
                    // Implicitly no request body
                    requestStream = new InboundContentLengthStream(this, 0);
                }
                else
                {
                    throw new NotImplementedException();
                }

                requestContent.SetStream(requestStream);
                request.Content = requestContent;
                return request;
            }

            public async Task SendResponse(HttpResponseMessage response,
                CancellationToken cancellationToken)
            {
                HttpContent responseContent = response.Content;

                // Determine if we need transfer encoding
                bool transferEncoding = false;
                long contentLength = 0;
                if (responseContent != null)
                {
                    if (response.Headers.TransferEncodingChunked == true)
                    {
                        transferEncoding = true;
                        throw new NotImplementedException();
                    }
                    else if (responseContent.Headers.ContentLength != null)
                    {
                        contentLength = responseContent.Headers.ContentLength.Value;
                    }
                    else
                    {
#if false
                        // TODO: Is this the correct behavior?
                        // Seems better to do TryComputeLength on the content first
                        Trace($"TransferEncodingChunked = {request.Headers.TransferEncodingChunked} but no Content-Length set; setting it to true");
                        request.Headers.TransferEncodingChunked = true;
                        transferEncoding = true;
#endif
                        throw new NotImplementedException();
                    }
                }

                // Start sending the response
                // TODO: can certainly make this more efficient...

                // Write response line
                await WriteStringAsync("HTTP/1.1 ");
                await WriteStringAsync(((int)response.StatusCode).ToString());
                await WriteStringAsync(" ");
                await WriteStringAsync(response.StatusCode.ToString());
                await WriteStringAsync("\r\n");

                // Write headers
                await WriteHeadersAsync(response.Headers);

                if (responseContent != null)
                {
                    await WriteHeadersAsync(responseContent.Headers);
                }

#if false
                // Also write out a Content-Length: 0 header if no request body, and this is a method that can have one.
                if (responseContent == null)
                {
                    if (request.Method != HttpMethod.Get &&
                        request.Method != HttpMethod.Head)
                    {
                        await WriteStringAsync("Content-Length: 0\r\n");
                    }
                }
#endif

                // CRLF for end of headers.
                await WriteStringAsync("\r\n");

                // Flush headers.  We need to do this if we have no body anyway.
                // TODO: Don't think we should always do this, consider...
//                await FlushAsync(cancellationToken);

                // Write body, if any
                if (responseContent != null)
                {
                    if (transferEncoding)
                    {
#if false
                        var stream = new ChunkedEncodingRequestStream(this);
                        await request.Content.CopyToAsync(stream);
                        await stream.CompleteAsync();
#endif
                    }
                    else
                    {
                        var stream = new OutboundContentLengthStream(this, (int)contentLength);
                        await response.Content.CopyToAsync(stream);
                    }
                }
            }

            private async Task WriteHeadersAsync(HttpHeaders headers)
            {
                foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
                {
                    await WriteStringAsync(header.Key);
                    await WriteStringAsync(": ");

                    bool first = true;
                    foreach (string headerValue in header.Value)
                    {
                        if (first)
                        {
                            first = false;
                        }
                        else
                        {
                            await WriteStringAsync(", ");
                        }
                        await WriteStringAsync(headerValue);
                    }

                    Debug.Assert(!first, "No values for header??");

                    await WriteStringAsync("\r\n");
                }
            }

            // Not sure this is the most efficient way, consider...
            // Given that it's UTF8, maybe we should implement this ourselves.
            private unsafe bool EncodeString(string s, ref int charOffset)
            {
                int charsUsed;
                int bytesUsed;
                bool done;

                fixed (char* c = s)
                fixed (byte* b = &_writeBuffer[_writeOffset])
                {
                    _encoder.Convert(c + charOffset, s.Length - charOffset, b, BufferSize - _writeOffset, true, out charsUsed, out bytesUsed, out done);
                }

                charOffset += charsUsed;
                _writeOffset += bytesUsed;

                return done;
            }

            // TODO: Avoid this if/when we can, since UTF8 encoding sucks
            // TODO: Consider using ValueTask here
            private async Task WriteStringAsync(string s)
            {
                int charOffset = 0;
                while (true)
                {
                    if (EncodeString(s, ref charOffset))
                    {
                        return;
                    }

                    // TODO: Flow cancellation
                    await FlushAsync(CancellationToken.None);
                }
            }

            internal async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (count < BufferSize - _writeOffset)
                {
                    // It fits in the current buffer, so just copy
                    Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeOffset, count);
                    _writeOffset += count;
                    return;
                }

                // Doesn't fit, so just flush and write directly
                // TODO: Be smarter than this.  If the write is just 2 bytes past the end, we shouldn't do this.
                // TODO: Also, we should do a gather write here when appropriate.
                await FlushAsync(cancellationToken);

                await _stream.WriteAsync(buffer, offset, count, cancellationToken);
            }

            private async Task FlushAsync(CancellationToken cancellationToken)
            {
                if (s_trace)
                {
                    var s = new UTF8Encoding(false).GetString(_writeBuffer, 0, _writeOffset);
                    Trace("Sending:\n" + s);
                }

                await _stream.WriteAsync(_writeBuffer, 0, _writeOffset, cancellationToken);
                _writeOffset = 0;
            }

        }


        private sealed class OutboundContentLengthStream : WriteOnlyStream
        {
            private HttpServerConnection _connection;
            private int _contentBytesRemaining;

            public OutboundContentLengthStream(HttpServerConnection connection, int contentBytesRemaining)
            {
                _connection = connection;
                _contentBytesRemaining = contentBytesRemaining;
            }

            // TODO: Need to consider how buffering works between here and the Connection proper...

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                // Don't write if nothing was given
                if (count == 0)
                {
                    return;
                }

                // If we've already written the whole body, just ignore this
                if (_connection == null)
                {
                    return;
                }

                count = Math.Min(count, _contentBytesRemaining);

                await _connection.WriteAsync(buffer, offset, count, cancellationToken);

                _contentBytesRemaining -= count;
                if (_contentBytesRemaining == 0)
                {
                    // TODO: Notify connection
                    _connection = null;
                }
            }
        }


        private sealed class InboundContentLengthStream : ReadOnlyStream
        {
            private HttpServerConnection _connection;
            private int _contentBytesRemaining;

            public InboundContentLengthStream(HttpServerConnection connection, int contentLength)
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
//                    _connection.ResponseBodyCompleted();
                    _connection = null;
                    return 0;
                }

                if (count > _contentBytesRemaining)
                {
                    count = _contentBytesRemaining;
                }

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
    }
}
