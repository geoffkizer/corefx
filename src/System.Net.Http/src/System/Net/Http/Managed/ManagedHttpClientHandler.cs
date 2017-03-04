// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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

        private HttpMessageHandler _handler;

        private ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool> _connectionPoolTable = new ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool>();

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
            if (_handler != null)
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
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;

                // Close all open connections
                // TODO: There's a timing issue here
                // Revisit when we improve the connection pooling implementation
                foreach (HttpConnectionPool connectionPool in _connectionPoolTable.Values)
                {
                    connectionPool.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        #endregion De/Constructors



        #region Request Execution

        private sealed class HttpConnectionPool : IDisposable
        {
            ConcurrentDictionary<HttpConnection, HttpConnection> _activeConnections;
            ConcurrentBag<HttpConnection> _idleConnections;
            bool _disposed;

            public HttpConnectionPool()
            {
                _activeConnections = new ConcurrentDictionary<HttpConnection, HttpConnection>();
                _idleConnections = new ConcurrentBag<HttpConnection>();
            }

            public HttpConnection GetConnection()
            {
                HttpConnection connection;
                if (_idleConnections.TryTake(out connection))
                {
                    if (!_activeConnections.TryAdd(connection, connection))
                    {
                        throw new InvalidOperationException();
                    }

                    return connection;
                }

                return null;
            }

            public void AddConnection(HttpConnection connection)
            {
                if (!_activeConnections.TryAdd(connection, connection))
                {
                    throw new InvalidOperationException();
                }
            }

            public void PutConnection(HttpConnection connection)
            {
                HttpConnection unused;
                if (!_activeConnections.TryRemove(connection, out unused))
                {
                    throw new InvalidOperationException();
                }

                _idleConnections.Add(connection);
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;

                    foreach (HttpConnection connection in _activeConnections.Keys)
                    {
                        connection.Dispose();
                    }

                    foreach (HttpConnection connection in _idleConnections)
                    {
                        connection.Dispose();
                    }
                }
            }
        }

        private sealed class HttpConnection : IDisposable
        {
            private const int BufferSize = 4096;

            ManagedHttpClientHandler _handler;
            private HttpConnectionKey _key;
            private TcpClient _client;
            private Stream _stream;
            private TransportContext _transportContext;
            private Uri _proxyUri;

            private StringBuilder _sb;

            private byte[] _writeBuffer;
            private int _writeOffset;

            private byte[] _readBuffer;
            private int _readOffset;
            private int _readLength;

            private bool _disposed;

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

            public HttpConnection(ManagedHttpClientHandler handler, HttpConnectionKey key, TcpClient client, Stream stream, TransportContext transportContext, Uri proxyUri)
            {
                _handler = handler;
                _key = key;
                _client = client;
                _stream = stream;
                _transportContext = transportContext;
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

                // Eat the rest of the line to CRLF
                // TODO: Set reason phrase
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

                var responseContent = new NetworkContent(CancellationToken.None);

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
                        if (s_trace) Trace($"TransferEncodingChunked = {request.Headers.TransferEncodingChunked} but no Content-Length set; setting it to true");
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

                // Start sending the request
                // TODO: can certainly make this more efficient...

                // Write request line
                await WriteStringAsync(request.Method.Method, cancellationToken);
                await WriteCharAsync(' ', cancellationToken);

                if (_proxyUri != null)
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

                // TODO: Flow cancellation
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

            // TODO: Avoid this if/when we can, since UTF8 encoding sucks
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
                if (s_trace)
                {
                    var s = new UTF8Encoding(false).GetString(_writeBuffer, 0, _writeOffset);
                    if (s_trace) Trace("Sending:\n" + s);
                }

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
            Uri connectUri = request.RequestUri;
            if (proxyUri != null)
            {
                if (connectUri.Scheme == "https")
                {
                    throw new NotImplementedException("no SSL proxy support");
                }

                connectUri = proxyUri;
            }

            // Simple connection pool.
            // We never expire connections.
            // That's unfortunate, but allows for reasonable perf testing for now.

            HttpConnectionKey key = new HttpConnectionKey(connectUri);

            HttpConnectionPool pool;
            if (_connectionPoolTable.TryGetValue(key, out pool))
            {
                HttpConnection poolConnection = pool.GetConnection();
                if (poolConnection != null)
                {
                    return poolConnection;
                }
            }

            // Connect
            TcpClient client;

            if (s_trace) Trace($"Creating new connection for {key}");

            try
            {
                // You would think TcpClient.Connect would just do this, but apparently not.
                // It works for IPv4 addresses but seems to barf on IPv6.
                // I need to explicitly invoke the constructor with AddressFamily = IPv6.
                // Probably worth further investigation....
                IPAddress ipAddress;
                if (IPAddress.TryParse(connectUri.Host, out ipAddress))
                {
                    client = new TcpClient(ipAddress.AddressFamily);
                    await client.ConnectAsync(ipAddress, connectUri.Port);
                }
                else
                {
                    client = new TcpClient();
                    await client.ConnectAsync(connectUri.Host, connectUri.Port);
                }

            }
            catch (SocketException se)
            {
                throw new HttpRequestException("could not connect", se);
            }

            client.NoDelay = true;

            Stream stream = client.GetStream();
            TransportContext transportContext = null;

            if (connectUri.Scheme == "https")
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

                // Note, the sslStream owns the underlying network stream.
                SslStream sslStream = new SslStream(stream, false, callback);

                try
                {
                    await sslStream.AuthenticateAsClientAsync(connectUri.Host, _clientCertificates, _sslProtocols, _checkCertificateRevocationList);
                    await sslStream.AuthenticateAsClientAsync(connectUri.Host, null, _sslProtocols, _checkCertificateRevocationList);
                }
                catch (AuthenticationException ae)
                {
                    // TODO: Tests expect HttpRequestException here.  Is that correct behavior?
                    sslStream.Dispose();
                    throw new HttpRequestException("could not establish SSL connection", ae);
                }
                catch (IOException ie)
                {
                    // TODO: Tests expect HttpRequestException here.  Is that correct behavior?
                    sslStream.Dispose();
                    throw new HttpRequestException("could not establish SSL connection", ie);
                }
                catch (Exception)
                {
                    sslStream.Dispose();
                    throw;
                }

                stream = sslStream;
                transportContext = sslStream.TransportContext;
            }

            var connection = new HttpConnection(this, key, client, stream, transportContext, proxyUri);

            // Add to list of active connections in pool
            if (pool == null)
            {
                pool = _connectionPoolTable.GetOrAdd(key, new HttpConnectionPool());
            }

            pool.AddConnection(connection);

            return connection;
        }

        private void ReturnConnectionToPool(HttpConnection connection)
        {
            HttpConnectionPool pool;
            if (!_connectionPoolTable.TryGetValue(connection.Key, out pool))
            {
                throw new InvalidOperationException();
            }

            pool.PutConnection(connection);

            if (s_trace) Trace($"Connection returned to pool for {connection.Key}");
        }

        private async Task<HttpResponseMessage> SendAsyncInternal(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Version.Major != 1 || request.Version.Minor != 1)
            {
                throw new PlatformNotSupportedException($"Only HTTP 1.1 supported -- request.Version was {request.Version}");
            }

            Uri proxyUri = null;
            if (_useProxy && _proxy != null)
            {
                proxyUri = GetProxyUri(_proxy, request.RequestUri);
            }

            HttpConnection connection = await GetOrCreateConnection(request, proxyUri, cancellationToken);

            HttpResponseMessage response = await connection.SendAsync(request, cancellationToken);

            return response;
        }

        private void SetupHandlerChain()
        {
            Debug.Assert(_handler == null);

            HttpMessageHandler handler = new InternalHandler(this);

            if (_useProxy && _proxy != null && _proxy.Credentials != null)
            {
                handler = new ProxyAuthenticationHandler(_proxy, handler);
            }

            if (_credentials != null)
            {
                handler = new AuthenticationHandler(_preAuthenticate, _credentials, handler);
            }

            if (_useCookies)
            {
                handler = new CookieHandler(CookieContainer, handler);
            }

            if (_allowAutoRedirect)
            {
                handler = new AutoRedirectHandler(_maxAutomaticRedirections, handler);
            }

            if (_automaticDecompression != DecompressionMethods.None)
            {
                handler = new DecompressionHandler(_automaticDecompression, handler);
            }

            _handler = handler;
        }

        protected internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ManagedHttpClientHandler));
            }

            if (_handler == null)
            {
                SetupHandlerChain();
            }

            return _handler.SendAsync(request, cancellationToken);
        }

        // TODO: Refactor

        private sealed class InternalHandler : HttpMessageHandler
        {
            ManagedHttpClientHandler _clientHandler;

            public InternalHandler(ManagedHttpClientHandler clientHandler)
            {
                _clientHandler = clientHandler;
            }

            protected internal override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return _clientHandler.SendAsyncInternal(request, cancellationToken);
            }
        }

        internal static Uri GetProxyUri(IWebProxy proxy, Uri requestUri)
        {
            Debug.Assert(proxy != null);
            Debug.Assert(requestUri != null);
            Debug.Assert(requestUri.IsAbsoluteUri);

            try
            {
                if (!proxy.IsBypassed(requestUri))
                {
                    return proxy.GetProxy(requestUri);
                }
            }
            catch (Exception)
            {
                // Eat any exception from the IWebProxy and just treat it as no proxy.
            }

            return null;
        }

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
