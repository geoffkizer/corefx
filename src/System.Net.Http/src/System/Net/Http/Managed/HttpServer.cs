// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Sockets;
using System.Threading.Tasks;

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
#if false
            Task.Run(() =>
            {
                var connection = new HttpServerConnection(this, client);
                connection.Run();
            });
#endif
        }
    }
}
