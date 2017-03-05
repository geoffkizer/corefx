// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
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
        private SslProtocols _sslProtocols = SslProtocols.None;
        private IDictionary<String, object> _properties;

        private HttpMessageHandler _handler;
        private volatile bool _disposed;

        private static bool s_trace = false;

        private static void Trace(string msg)
        {
            if (s_trace)
            {
                Console.WriteLine(msg);
            }
        }

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

        public ManagedHttpClientHandler()
        {
        }

        protected override void Dispose(bool disposing)
        {

            if (disposing && !_disposed)
            {
                _disposed = true;

                _handler?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SetupHandlerChain()
        {
            Debug.Assert(_handler == null);

            HttpMessageHandler handler = new HttpConnectionHandler(
                _clientCertificates,
                _serverCertificateCustomValidationCallback,
                _checkCertificateRevocationList,
                _sslProtocols,
                _useProxy ? _proxy : null);

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

        // Static utility routine; move somewhere else?
        // TODO: Move this

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
                // TODO: This seems a bit questionable, but it's what the tests expect
            }

            return null;
        }
    }

    // TODO: Move
    // TODO: Refactor proxy handling into HttpProxyConnectionHandler
    // TODO: Move proxy auth to HttpProxyConnectionHandler

    internal sealed class HttpConnectionHandler : HttpMessageHandler
    {
        private readonly IWebProxy _proxy;
        private readonly X509CertificateCollection _clientCertificates;
        private readonly Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> _serverCertificateCustomValidationCallback;
        private readonly bool _checkCertificateRevocationList;
        private readonly SslProtocols _sslProtocols;

        private readonly ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool> _connectionPoolTable;
        private bool _disposed;

        public HttpConnectionHandler(
            X509CertificateCollection clientCertificates,
            Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> serverCertificateCustomValidationCallback,
            bool checkCertificateRevocationList,
            SslProtocols sslProtocols,
            IWebProxy proxy)
        {
            _proxy = proxy;
            _clientCertificates = clientCertificates;
            _serverCertificateCustomValidationCallback = serverCertificateCustomValidationCallback;
            _checkCertificateRevocationList = checkCertificateRevocationList;
            _sslProtocols = sslProtocols;

            _connectionPoolTable = new ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool>();
        }

        protected internal override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Version.Major != 1 || request.Version.Minor != 1)
            {
                throw new PlatformNotSupportedException($"Only HTTP 1.1 supported -- request.Version was {request.Version}");
            }

            Uri proxyUri = null;
            if (_proxy != null)
            {
                proxyUri = ManagedHttpClientHandler.GetProxyUri(_proxy, request.RequestUri);
            }

            HttpConnection connection = await GetOrCreateConnection(request, proxyUri, cancellationToken);

            HttpResponseMessage response = await connection.SendAsync(request, cancellationToken);

            return response;
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

            NetworkStream networkStream = client.GetStream();

#if false
            // Set default read/write timeouts of 5 seconds.
            // TODO: Make these configurable?
            networkStream.ReadTimeout = 5000;
            networkStream.WriteTimeout = 5000;
#endif

            Stream stream = networkStream;
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

            if (pool == null)
            {
                pool = _connectionPoolTable.GetOrAdd(key, new HttpConnectionPool());
            }

            var connection = new HttpConnection(pool, key, client, stream, transportContext, (proxyUri != null));

            return connection;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !!_disposed)
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
    }
}
