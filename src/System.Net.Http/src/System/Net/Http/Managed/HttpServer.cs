// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.Managed
{
    public sealed class HttpServer
    {
        private IPEndPoint _ipEndpoint;
        private HttpMessageHandler _handler;
        private HttpServerConnectionManager _manager;

        private static bool s_trace = false;

        private static void Trace(string msg)
        {
            if (s_trace)
            {
                Console.WriteLine(msg);
            }
        }

        public HttpServer(IPEndPoint ipEndpoint, HttpMessageHandler handler)
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

        // TODO: This class has become a no-op.
        // I thought I might need it to do some connection management, but it doesn't seem
        // like there's any value to it at this point...

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

            public override void PutConnection(HttpConnection connection)
            {
            }
        }

        private void HandleConnection(HttpConnection connection, CancellationToken cancellationToken)
        {
            Task.Run(() => ReceiveAsync(connection, cancellationToken));
        }

        private async void ReceiveAsync(HttpConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await connection.ReceiveAsync(_handler, cancellationToken);

                    // If there is pending read data, then it must be a subsequent pipelined request;
                    // If not, then flush now.
                    if (!connection.HasBufferedReadBytes)
                    {
                        await connection.FlushAsync(CancellationToken.None);
                    }
                }
            }
            catch (IOException)
            {
                // Client has disconnected.  Eat the exception.
            }
        }
    }
}
