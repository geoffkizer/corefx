// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Managed
{
    public class ManagedHttpClientHandler : HttpMessageHandler
    {
        // Configuration settings
        private bool _useCookies = true;
        private CookieContainer _cookieContainer;
        private ClientCertificateOption _clientCertificateOptions = ClientCertificateOption.Manual;
        private DecompressionMethods _automaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        private bool _useProxy = true;
        private IWebProxy _proxy;
        private ICredentials _defaultProxyCredentials;
        private bool _preAuthenticate = false;
        private bool _useDefaultCredentials = false;
        private ICredentials _credentials;
        private bool _allowAutoRedirect = true;
        private int _maxAutomaticRedirections = 50;
        private int _maxResponseHeadersLength = 64 * 1024;
        private int _maxConnectionsPerServer = int.MaxValue;
        private X509CertificateCollection _clientCertificates;
        private Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> _serverCertificateCustomValidationCallback;
        private bool _checkCertificateRevocationList = false;
        private SslProtocols _sslProtocols;         // TODO: Default?
        private IDictionary<String, object> _properties;

        private bool _inUse = false;

        private ConcurrentDictionary<HttpConnectionKey, ConcurrentBag<HttpConnection>> _connectionPoolTable = new ConcurrentDictionary<HttpConnectionKey, ConcurrentBag<HttpConnection>>();

        private static StringWithQualityHeaderValue s_gzipHeaderValue = new StringWithQualityHeaderValue("gzip");
        private static StringWithQualityHeaderValue s_deflateHeaderValue = new StringWithQualityHeaderValue("deflate");

        private static bool s_trace = false;

        // Enabling this for perf testing; probably want to disable it
        private static bool s_sendKeepAlive = true;

        private static void Trace(string msg)
        {
            if (s_trace)
            {
                Console.WriteLine(msg);
            }
        }

        #region Properties

        private void CheckInUse()
        {
            // Can't set props once in use
            if (_inUse)
            {
                throw new InvalidOperationException();
            }
        }

        public virtual bool SupportsAutomaticDecompression
        {
            get { return true; }
        }

        public virtual bool SupportsProxy
        {
            get { return true; }
        }

        public virtual bool SupportsRedirectConfiguration
        {
            get { return true; }
        }

        public bool UseCookies
        {
            get { return _useCookies; }
            set { CheckInUse(); _useCookies = value; }
        }

        public CookieContainer CookieContainer
        {
            get
            {
                if (_cookieContainer == null)
                {
                    _cookieContainer = new CookieContainer();
                }

                return _cookieContainer;
            }
            set { CheckInUse(); _cookieContainer = value; }
        }

        public ClientCertificateOption ClientCertificateOptions
        {
            get { return _clientCertificateOptions; }
            set
            {
                CheckInUse();
                if (value == ClientCertificateOption.Automatic || value == ClientCertificateOption.Manual)
                {
                    _clientCertificateOptions = value;
                    return;
                }

                throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        public DecompressionMethods AutomaticDecompression
        {
            get { return _automaticDecompression; }
            set { CheckInUse(); _automaticDecompression = value; }
        }

        public bool UseProxy
        {
            get { return _useProxy; }
            set { CheckInUse(); _useProxy = value; }
        }

        public IWebProxy Proxy
        {
            get { return _proxy; }
            set { CheckInUse(); _proxy = value; }
        }

        public ICredentials DefaultProxyCredentials
        {
            get { return _defaultProxyCredentials; }
            set { CheckInUse(); _defaultProxyCredentials = value; }
        }

        public bool PreAuthenticate
        {
            get { return _preAuthenticate; }
            set { CheckInUse(); _preAuthenticate = value; }
        }

        public bool UseDefaultCredentials
        {
            get { return _useDefaultCredentials; }
            set { CheckInUse(); _useDefaultCredentials = value; }
        }

        public ICredentials Credentials
        {
            get { return _credentials; }
            set { CheckInUse(); _credentials = value; }
        }

        public bool AllowAutoRedirect
        {
            get { return _allowAutoRedirect; }
            set { CheckInUse(); _allowAutoRedirect = value; }
        }

        public int MaxAutomaticRedirections
        {
            get { return _maxAutomaticRedirections; }
            set { CheckInUse(); _maxAutomaticRedirections = value; }
        }

        public int MaxConnectionsPerServer
        {
            get { return _maxConnectionsPerServer; }
            set
            {
                CheckInUse();
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _maxConnectionsPerServer = value;
            }
        }

        public int MaxResponseHeadersLength
        {
            get { return _maxResponseHeadersLength; }
            set
            {
                CheckInUse();
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _maxResponseHeadersLength = value;
            }
        }

        public X509CertificateCollection ClientCertificates
        {
            get
            {
                if (_clientCertificates == null)
                {
                    _clientCertificates = new X509CertificateCollection();
                }

                return _clientCertificates;
            }
        }

        public Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> ServerCertificateCustomValidationCallback
        {
            get { return _serverCertificateCustomValidationCallback; }
            set { CheckInUse(); _serverCertificateCustomValidationCallback = value; }
        }

        public bool CheckCertificateRevocationList
        {
            get { return _checkCertificateRevocationList; }
            set { CheckInUse(); _checkCertificateRevocationList = value; }
        }

        public SslProtocols SslProtocols
        {
            get { return _sslProtocols; }
            set
            {
                CheckInUse();
#pragma warning disable 0618 // obsolete warning
                if ((value & (SslProtocols.Ssl2 | SslProtocols.Ssl3)) != 0)
                {
                    throw new NotSupportedException("unsupported SSL protocols");
                }
#pragma warning restore 0618

                _sslProtocols = value;
            }
        }

        public IDictionary<String, object> Properties
        {
            get
            {
                if (_properties == null)
                {
                    _properties = new Dictionary<string, object>();
                }

                return _properties;
            }
        }

        #endregion Properties

        #region De/Constructors

        public ManagedHttpClientHandler()
        {
#if false
            // Adjust defaults to match current .NET Desktop HttpClientHandler (based on HWR stack).
            AllowAutoRedirect = true;
            UseProxy = true;
            UseCookies = true;
            CookieContainer = new CookieContainer();
            _winHttpHandler.DefaultProxyCredentials = null;
            _winHttpHandler.ServerCredentials = null;
#endif

#if false
            // The existing .NET Desktop HttpClientHandler based on the HWR stack uses only WinINet registry
            // settings for the proxy.  This also includes supporting the "Automatic Detect a proxy" using
            // WPAD protocol and PAC file. So, for app-compat, we will do the same for the default proxy setting.
            _winHttpHandler.WindowsProxyUsePolicy = WindowsProxyUsePolicy.UseWinInetProxy;
            _winHttpHandler.Proxy = null;
#endif
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        #endregion De/Constructors



        #region Request Execution

        private sealed class HttpConnection
        {
            private const int BufferSize = 4096;

            ManagedHttpClientHandler _handler;
            private HttpConnectionKey _key;
            private TcpClient _client;
            private Stream _stream;
            private Uri _proxyUri;

            private StringBuilder _sb;

            private byte[] _writeBuffer;
            private int _writeOffset;

            private byte[] _readBuffer;
            private int _readOffset;
            private int _readLength;

            private sealed class ContentLengthResponseStream : ReadOnlyStream
            {
                private HttpConnection _connection;
                private int _contentBytesRemaining;

                public ContentLengthResponseStream(HttpConnection connection, int contentLength)
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
                        byte b = await _connection.ReadByteAsync();

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

                            b = await _connection.ReadByteAsync();
                            if (b == (byte)'\r')
                            {
                                if (await _connection.ReadByteAsync() != (byte)'\n')
                                    throw new IOException("Saw CR without LF while parsing chunk size");

                                break;
                            }
                        }

                        if (chunkSize == 0)
                        {
                            // Indicates end of response body

                            // We expect final CRLF after this
                            if (await _connection.ReadByteAsync() != (byte)'\r' ||
                                await _connection.ReadByteAsync() != (byte)'\n')
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
                        if (await _connection.ReadByteAsync() != (byte)'\r' ||
                            await _connection.ReadByteAsync() != (byte)'\n')
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
                        _connection.Close();
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
                    await _connection.WriteAsync(_chunkLengthBuffer, digit, (sizeof(int)) - digit);
                    await _connection.WriteAsync(_CRLF, 0, _CRLF.Length);

                    // Write chunk contents
                    await _connection.WriteAsync(buffer, offset, count);
                    await _connection.WriteAsync(_CRLF, 0, _CRLF.Length);
                }

                public async Task CompleteAsync()
                {
                    // Send 0 byte chunk to indicate end
                    _chunkLengthBuffer[0] = (byte)'0';
                    await _connection.WriteAsync(_chunkLengthBuffer, 0, 1);
                    await _connection.WriteAsync(_CRLF, 0, _CRLF.Length);

                    // Send final _CRLF
                    await _connection.WriteAsync(_CRLF, 0, _CRLF.Length);

                    // Flush underlying connection buffer
                    await _connection.FlushAsync(CancellationToken.None);

                    _connection = null;
                }
            }

            public HttpConnection(ManagedHttpClientHandler handler, HttpConnectionKey key, TcpClient client, Stream stream, Uri proxyUri)
            {
                _handler = handler;
                _key = key;
                _client = client;
                _stream = stream;
                _proxyUri = proxyUri;

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

            public void Close()
            {
                _stream.Close();
                _client.Close();
            }

            private async Task<HttpResponseMessage> ParseResponse(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                HttpResponseMessage response = new HttpResponseMessage();
                response.RequestMessage = request;

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

                // Don't think this is correct, here and below
                byte b = await ReadByteAsync();
                while (b == (byte)' ')
                    b = await ReadByteAsync();

                byte status1 = b;
                byte status2 = await ReadByteAsync();
                byte status3 = await ReadByteAsync();

                if (!char.IsDigit((char)status1) ||
                    !char.IsDigit((char)status2) ||
                    !char.IsDigit((char)status3))
                {
                    throw new HttpRequestException("could not read response status code");
                }

                int status = 100 * (status1 - (byte)'0') + 10 * (status2 - (byte)'0') + (status3 - (byte)'0');
                response.StatusCode = (HttpStatusCode)status;

                b = await ReadByteAsync();
                if (b == (byte)' ')
                {
                    // Eat the rest of the line to CRLF
                    // TODO: Set reason phrase
                    b = await ReadByteAsync();
                    while (b != (byte)'\r')
                        b = await ReadByteAsync();

                }
                else if (b != (byte)'\r')
                {
                    throw new HttpRequestException("Could not parse status code");
                }

                b = await ReadByteAsync();
                if (b != (byte)'\n')
                    throw new HttpRequestException("Saw CR without LF while parsing response line");

                var responseContent = new NetworkContent(CancellationToken.None);

                // Parse headers
                _sb.Clear();
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
                        _sb.Append((char)b);
                        b = await ReadByteAsync();
                    }

                    string headerName = _sb.ToString();

                    _sb.Clear();

                    // Get header value
                    b = await ReadByteAsync();
                    while (b == (byte)' ')
                    {
                        b = await ReadByteAsync();
                    }

                    while (b != (byte)'\r')
                    {
                        _sb.Append((char)b);
                        b = await ReadByteAsync();
                    }

                    if (await ReadByteAsync() != (byte)'\n')
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

                    b = await ReadByteAsync();
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
                        Trace($"TransferEncodingChunked = {request.Headers.TransferEncodingChunked} but no Content-Length set; setting it to true");
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

                if (s_sendKeepAlive)
                {
                    // Add Connection: Keep-Alive, for perf comparison
                    // Temporary
                    request.Headers.Connection.Add("Keep-Alive");
                }

                // Add Accept-Encoding for compression
                // TODO: Move elsewhere
                if ((_handler._automaticDecompression & DecompressionMethods.GZip) != 0)
                {
                    request.Headers.AcceptEncoding.Add(s_gzipHeaderValue);
                }
                if ((_handler._automaticDecompression & DecompressionMethods.Deflate) != 0)
                {
                    request.Headers.AcceptEncoding.Add(s_deflateHeaderValue);
                }

                // Start sending the request
                // TODO: can certainly make this more efficient...

                // Write request line
                await WriteStringAsync(request.Method.Method);
                await WriteStringAsync(" ");

                if (_proxyUri != null)
                {
                    await WriteStringAsync(request.RequestUri.AbsoluteUri);
                }
                else
                {
                    await WriteStringAsync(request.RequestUri.PathAndQuery);
                }

                await WriteStringAsync(" HTTP/1.1\r\n");

                // Write headers
                await WriteHeadersAsync(request.Headers);

                if (requestContent != null)
                {
                    await WriteHeadersAsync(requestContent.Headers);
                }

                // Also write out a Content-Length: 0 header if no request body, and this is a method that can have one.
                if (requestContent == null)
                {
                    if (request.Method != HttpMethod.Get &&
                        request.Method != HttpMethod.Head)
                    {
                        await WriteStringAsync("Content-Length: 0\r\n");
                    }
                }

                // CRLF for end of headers.
                await WriteStringAsync("\r\n");

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
                        await request.Content.CopyToAsync(stream);
                        await stream.CompleteAsync();
                    }
                    else
                    {
                        // TODO: Is it fine to just pass the underlying stream?
                        // For small content, seems like this should go through buffering
                        await request.Content.CopyToAsync(_stream);
                    }
                }

                return await ParseResponse(request, cancellationToken);
            }

            private async Task WriteAsync(byte[] buffer, int offset, int count)
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

                // TODO: Flow cancellation
                await FlushAsync(CancellationToken.None);

                // Update offset and count to reflect the write we just did.
                offset += remaining;
                count -= remaining;

                if (count >= BufferSize)
                {
                    // Large write.  No sense buffering this.  Write directly to stream.
                    // CONSIDER: May want to be a bit smarter here?  Think about how large writes should work...
                    await _stream.WriteAsync(buffer, offset, count);
                }
                else
                {
                    // Copy remainder into buffer
                    Buffer.BlockCopy(buffer, offset, _writeBuffer, _writeOffset, 0);
                    _writeOffset += count;
                }
            }

            private ValueTask<bool> WriteCharAsync(char c)
            {
                if ((c & 0xFF80) != 0)
                {
                    throw new HttpRequestException("Non-ASCII characters found");
                }

                return WriteByteAsync((byte)c);
            }

            private ValueTask<bool> WriteByteAsync(byte b)
            {
                if (_writeOffset < BufferSize)
                {
                    _writeBuffer[_writeOffset++] = b;
                    return new ValueTask<bool>(true);
                }

                return new ValueTask<bool>(WriteByteSlowAsync(b));
            }

            private async Task<bool> WriteByteSlowAsync(byte b)
            {
                await _stream.WriteAsync(_writeBuffer, 0, BufferSize);

                _writeBuffer[0] = b;
                _writeOffset = 1;

                return true;
            }

            // TODO: Avoid this if/when we can, since UTF8 encoding sucks
            // TODO: Consider using ValueTask here
            private async Task WriteStringAsync(string s)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    await WriteCharAsync(s[i]);
                }
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

            private async Task<byte> ReadByteSlowAsync()
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

                return new ValueTask<byte>(ReadByteSlowAsync());
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

            private void ResponseBodyCompleted()
            {
                // Called by the response stream when the body has been fully read

                // Put the connection back in the connection pool, for reuse
                _handler.ReturnConnectionToPool(this);
            }
        }

        private struct HttpConnectionKey : IEquatable<HttpConnectionKey>
        {
            public readonly string Scheme;
            public readonly string Host;
            public readonly int Port;

            public HttpConnectionKey(Uri uri)
            {
                Scheme = uri.Scheme;
                Host = uri.Host;
                Port = uri.Port;
            }

            public override int GetHashCode()
            {
                return Scheme.GetHashCode() ^ Host.GetHashCode() ^ Port.GetHashCode();
            }

            public override bool Equals(object obj)
            {
                if (obj == null || obj.GetType() != typeof(HttpConnectionKey))
                {
                    return false;
                }

                return Equals((HttpConnectionKey)obj);
            }

            public bool Equals(HttpConnectionKey other)
            {
                return (Scheme == other.Scheme && Host == other.Host && Port == other.Port);
            }

            public static bool operator ==(HttpConnectionKey key1, HttpConnectionKey key2)
            {
                return key1.Equals(key2);
            }

            public static bool operator !=(HttpConnectionKey key1, HttpConnectionKey key2)
            {
                return !key1.Equals(key2);
            }
        }

        private async Task<HttpConnection> GetOrCreateConnection(HttpRequestMessage request, Uri proxyUri, CancellationToken cancellationToken)
        {
            Uri uri = proxyUri ?? request.RequestUri;

            // Simple connection pool.
            // We never expire connections.
            // That's unfortunate, but allows for reasonable perf testing for now.

            HttpConnectionKey key = new HttpConnectionKey(uri);

            ConcurrentBag<HttpConnection> pool;
            if (_connectionPoolTable.TryGetValue(key, out pool))
            {
                HttpConnection poolConnection;
                if (pool.TryTake(out poolConnection))
                {
                    Trace($"Reusing pooled connection for {key}");
                    return poolConnection;
                }
            }

            // Connect
            TcpClient client;

            Trace($"Creating new connection for {key}");

            try
            {
                // You would think TcpClient.Connect would just do this, but apparently not.
                // It works for IPv4 addresses but seems to barf on IPv6.
                // I need to explicitly invoke the constructor with AddressFamily = IPv6.
                // Probably worth further investigation....
                IPAddress ipAddress;
                if (IPAddress.TryParse(uri.Host, out ipAddress))
                {
                    client = new TcpClient(ipAddress.AddressFamily);
                    await client.ConnectAsync(ipAddress, uri.Port);
                }
                else
                {
                    client = new TcpClient();
                    await client.ConnectAsync(uri.Host, uri.Port);
                }

            }
            catch (SocketException se)
            {
                throw new HttpRequestException("could not connect", se);
            }

            client.NoDelay = true;

            Stream stream = client.GetStream();

            if (uri.Scheme == "https")
            {
                if (proxyUri != null)
                    throw new NotImplementedException("no SSL proxy support");

                try
                {
                    RemoteCertificateValidationCallback callback = null;
                    if (_serverCertificateCustomValidationCallback != null)
                    {
                        callback = (object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) =>
                        {
                            // TODO: Not sure this is correct...
                            var cert2 = certificate as X509Certificate2;

                            return _serverCertificateCustomValidationCallback(request, cert2, chain, sslPolicyErrors);
                        };
                    }

                    SslStream sslStream = new SslStream(stream, false, callback);

                    await sslStream.AuthenticateAsClientAsync(uri.Host, null, _sslProtocols, _checkCertificateRevocationList);

                    stream = sslStream;
                }
                catch (AuthenticationException ae)
                {
                    throw new HttpRequestException("could not establish SSL connection", ae);
                }
            }

            return new HttpConnection(this, key, client, stream, proxyUri);
        }

        private void ReturnConnectionToPool(HttpConnection connection)
        {
            ConcurrentBag<HttpConnection> pool;
            if (!_connectionPoolTable.TryGetValue(connection.Key, out pool))
            {
                pool = _connectionPoolTable.GetOrAdd(connection.Key, new ConcurrentBag<HttpConnection>());
            }

            Trace($"Connection returned to pool for {connection.Key}");
            pool.Add(connection);
        }

        private async Task<HttpResponseMessage> SendAsync2(HttpRequestMessage request, Uri proxyUri,
            CancellationToken cancellationToken)
        {
            if (request.Version.Major != 1 || request.Version.Minor != 1)
            {
                throw new PlatformNotSupportedException($"Only HTTP 1.1 supported -- request.Version was {request.Version}");
            }

            HttpConnection connection = await GetOrCreateConnection(request, proxyUri, cancellationToken);

            // Add cookies
            // TODO: This isn't right, we should do this in the top-level handler
            if (_cookieContainer != null)
            {
                string cookieHeader = _cookieContainer.GetCookieHeader(request.RequestUri);
                if (cookieHeader != null)
                {
                    request.Headers.Add("Cookie", cookieHeader);
                }
            }

            HttpResponseMessage response = await connection.SendAsync(request, cancellationToken);

            return response;
        }

        private string GetBasicTokenForCredential(NetworkCredential credential)
        {
            if (credential.UserName.IndexOf(':') != -1)
            {
                // TODO: What's the right way to handle this?
//              throw new NotImplementedException($"Proxy auth: can't handle ':' in username \"{credential.UserName}\"");
            }

            string userPass = credential.UserName + ":" + credential.Password;
            if (!string.IsNullOrEmpty(credential.Domain))
            {
                if (credential.UserName.IndexOf(':') != -1)
                {
                    // TODO: What's the right way to handle this?
//                  throw new NotImplementedException($"Proxy auth: can't handle ':' in domain \"{credential.Domain}\"");
                }

                userPass = credential.Domain + "\\" + userPass;
            }

            return Convert.ToBase64String(Encoding.UTF8.GetBytes(userPass));
        }

        private async Task<HttpResponseMessage> HandleProxyAuthenticationAsync(
            HttpRequestMessage request, 
            HttpResponseMessage response,
            Uri proxyUri,
            CancellationToken cancellationToken)
        {
            HttpHeaderValueCollection<AuthenticationHeaderValue> proxyAuthenticateValues = response.Headers.ProxyAuthenticate;
            Trace($"407 received, ProxyAuthenticate: {proxyAuthenticateValues}");

            if (proxyUri == null)
            {
                throw new HttpRequestException("407 received but no proxy");
            }

            foreach (AuthenticationHeaderValue h in proxyAuthenticateValues)
            {
                // We only support Basic auth against a proxy, ignore others
                if (h.Scheme == "Basic")
                {
                    NetworkCredential credential = _proxy.Credentials.GetCredential(proxyUri, "Basic");

                    if (credential == null)
                    {
                        continue;
                    }

                    request.Headers.ProxyAuthorization = new AuthenticationHeaderValue("Basic",
                        GetBasicTokenForCredential(credential));

                    response = await SendAsync2(request, proxyUri, cancellationToken);

                    if (response.StatusCode != HttpStatusCode.ProxyAuthenticationRequired)
                    {
                        // Success
                        break;
                    }
                }
            }

            // Return the last response we received, successful or not
            return response;
        }

        private bool TrySetBasicAuthToken(HttpRequestMessage request)
        {
            NetworkCredential credential = _credentials.GetCredential(request.RequestUri, "Basic");
            if (credential == null)
            {
                return false;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", GetBasicTokenForCredential(credential));
            return true;
        }

        private async Task<HttpResponseMessage> HandleAuthenticationAsync(
            HttpRequestMessage request,
            HttpResponseMessage response,
            Uri proxyUri,
            CancellationToken cancellationToken)
        {
            HttpHeaderValueCollection<AuthenticationHeaderValue> authenticateValues = response.Headers.WwwAuthenticate;
            Trace($"401 received, WWW-Authenticate: {authenticateValues}");

            foreach (AuthenticationHeaderValue h in authenticateValues)
            {
                // We only support Basic auth, ignore others
                if (h.Scheme == "Basic")
                {
                    if (!TrySetBasicAuthToken(request))
                    {
                        continue;
                    }

                    response = await SendAsync2(request, proxyUri, cancellationToken);

                    if (response.StatusCode != HttpStatusCode.Unauthorized)
                    {
                        // Success
                        break;
                    }
                }
            }

            // Return the last response we received, successful or not
            return response;
        }

        private bool CheckForRedirect(HttpRequestMessage request, HttpResponseMessage response, ref int redirectCount)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Moved:
                case HttpStatusCode.TemporaryRedirect:
                    break;

                case HttpStatusCode.Found:
                case HttpStatusCode.SeeOther:
                    request.Method = HttpMethod.Get;
                    request.Content = null;
                    break;

                case HttpStatusCode.MultipleChoices:
                    if (response.Headers.Location == null)
                    {
                        // Don't redirect if no Location specified
                        return false;
                    }
                    break;

                default:
                    return false;
            }

            Uri location = response.Headers.Location;
            if (location == null)
            {
                throw new HttpRequestException("no Location header for redirect");
            }

            if (!location.IsAbsoluteUri)
            {
                location = new Uri(request.RequestUri, location);
            }

            request.RequestUri = location;

            redirectCount++;
            if (redirectCount > _maxAutomaticRedirections)
            {
                throw new Exception("max redirects exceeded");
            }

            return true;
        }

        protected internal override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _inUse = true;

            int redirectCount = 0;
            HttpResponseMessage response;
            while (true)
            {
                // Determine if we should use a proxy
                // CONSIDER: Factor into separate function
                Uri proxyUri = null;
                try
                {
                    if (_useProxy && _proxy != null && !_proxy.IsBypassed(request.RequestUri))
                    {
                        proxyUri = _proxy.GetProxy(request.RequestUri);
                    }
                }
                catch (Exception)
                {
                    // Tests expect exceptions from calls to _proxy to be eaten, apparently.
                    // TODO: What's the right behavior here?
                }

                // Preautheticate, if requested and we have credentials
                if (_preAuthenticate && _credentials != null)
                {
                    TrySetBasicAuthToken(request);
                }

                response = await SendAsync2(request, proxyUri, cancellationToken);

                // Handle proxy authentication
                if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired &&
                    _proxy.Credentials != null)
                {
                    response = await HandleProxyAuthenticationAsync(request, response, proxyUri, cancellationToken);
                }

                // Handle authentication, if we didn't preauthenticate above
                if (response.StatusCode == HttpStatusCode.Unauthorized &&
                    !_preAuthenticate &&
                    _credentials != null)
                {
                    response = await HandleAuthenticationAsync(request, response, proxyUri, cancellationToken);
                }

                // Handle redirect
                if (!_allowAutoRedirect ||
                    !CheckForRedirect(request, response, ref redirectCount))
                {
                    // No redirect
                    break;
                }
            }

            // Handle Set-Cookie
            // No typed property for this, apparently?
            IEnumerable<string> setCookies;
            if (response.Headers.TryGetValues("Set-Cookie", out setCookies))
            {
                foreach (string setCookie in setCookies)
                {
                    Trace($"Set-Cookie: {setCookie}");

                    CookieContainer.SetCookies(request.RequestUri, setCookie);
                }
            }

            return response;
        }

        // TODO: Flow cancellation consistently

#endregion Request Execution

#region Private

        private volatile bool _disposed;
#endregion Private

    }


    // Stuff below here is stuff that I think will be shared.
    // It should end up in another file somewhere.

    // This is based off NoWriteNoSeekStreamContent
    internal sealed class NetworkContent : HttpContent
    {
        private readonly CancellationToken _cancellationToken;
        private Stream _content;
        private bool _contentConsumed;

        public NetworkContent(CancellationToken cancellationToken)
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

    internal abstract class ReadOnlyStream : Stream
    {
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
    }

    internal abstract class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

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

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAsync(buffer, offset, count, CancellationToken.None).Wait();
        }
    }
}
