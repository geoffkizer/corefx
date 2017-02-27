using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.IO;

namespace System.Net.Http.Managed
{
    // TODO: Much code possibly shared with client

    public sealed class HttpServer
    {
        private IPEndPoint _ipEndpoint;
        private HttpClientHandler _handler;

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
            private HttpServer _server;
            private TcpClient _client;
            private Stream _stream;

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

            private async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
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
                    // Let's try setting the stream to null, what happens?
                {
                    // There is implicitly no response body
                    // TODO: Should this mean there's no responseContent at all?
                    responseStream = new ContentLengthResponseStream(this, 0);
                }
                else if (responseContent.Headers.ContentLength != null)
                {
                    // TODO: deal with very long content length
                    responseStream = new ContentLengthResponseStream(this, (int)responseContent.Headers.ContentLength.Value);
                }
                else if (response.Headers.TransferEncodingChunked == true)
                {
                    responseStream = new ChunkedEncodingResponseStream(this);
                }
                else
                {
                    responseStream = new ConnectionCloseResponseStream(this);
                }

                foreach (var s in responseContent.Headers.ContentEncoding)
                {
                    if (s == "gzip")
                    {
                        responseStream = new GZipStream(responseStream, CompressionMode.Decompress);
                    }
                    else if (s == "deflate")
                    {
                        responseStream = new DeflateStream(responseStream, CompressionMode.Decompress);
                    }
                    else
                    {
                        throw new IOException($"Unexpected Content-Encoding: {s}");
                    }
                }

                responseContent.SetStream(responseStream);
                response.Content = responseContent;
                return response;
            }



        }
    }
    }
}
