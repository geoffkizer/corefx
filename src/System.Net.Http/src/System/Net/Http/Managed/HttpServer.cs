// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Managed
{
    public sealed class HttpServer
    {
        private IPEndPoint _ipEndpoint;
        private HttpClientHandler _handler;
        private HttpServerConnectionManager _manager;

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

            _manager = new HttpServerConnectionManager(this);
        }

        public async void Run()
        {
            TcpListener listener = new TcpListener(_ipEndpoint);
            listener.Start();

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();
                HttpConnection connection = new HttpConnection(_manager, client.GetStream(), null, false);
                HandleConnection(connection, CancellationToken.None);
            }
        }

        private class HttpServerConnectionManager : HttpConnectionManager
        {
            private HttpServer _server;

            public HttpServerConnectionManager(HttpServer server)
            {
                _server = server;
            }

            public override void AddConnection(HttpConnection connection)
            {
            }

            public override async void PutConnection(HttpConnection connection)
            {
                // If there is pending read data, then it must be a subsequent pipelined request;
                // If not, then flush now.
                if (!connection.HasBufferedReadBytes)
                {
                    await connection.FlushAsync(CancellationToken.None);
                }

                await _server.ReceiveAsync(connection, CancellationToken.None);
            }
        }

        private void HandleConnection(HttpConnection connection, CancellationToken cancellationToken)
        {
            Task.Run(() => ReceiveAsync(connection, cancellationToken));

            // ReceiveAsync will process the incoming request and send the response
            // When complete, HttpServerConnectionManager.PutConnection will be called
        }

        private Task ReceiveAsync(HttpConnection connection, CancellationToken cancellationToken)
        {
            return connection.ReceiveAsync(_handler, cancellationToken);
        }
    }
}
