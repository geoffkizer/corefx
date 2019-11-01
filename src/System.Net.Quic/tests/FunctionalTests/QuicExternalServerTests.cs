// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Net.Quic;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace System.Net.Quic.Tests
{
    public class QuicExternalServerTests
    {
        // Disabled for now
        [Fact]
        public async Task QuicTechTest()
        {
            const string serverName = "quic.tech";
            const int port = 4433;

            IPAddress ipaddr = Dns.GetHostAddresses(serverName)[0];

            SslClientAuthenticationOptions sslOptions = new SslClientAuthenticationOptions();
            sslOptions.TargetHost = serverName;
            sslOptions.ApplicationProtocols = new List<SslApplicationProtocol>() { new SslApplicationProtocol("http/0.9") };

            // Client code
            using (QuicConnection connection = new QuicConnection(QuicImplementationProviders.Quiche, new IPEndPoint(ipaddr, port), sslOptions))
            {
                await connection.ConnectAsync();

                Console.WriteLine($"--- Connected. NegotiatedApplicationProtocol: {connection.NegotiatedApplicationProtocol}");

                using (QuicStream stream = connection.OpenBidirectionalStream())
                {
                    Console.WriteLine("--- About to call WriteAsync");

                    await stream.WriteAsync(Encoding.UTF8.GetBytes("GET /index.html\r\n"));

                    Console.WriteLine("--- WriteAsync complete. Waiting before shutting down stream");
                    Thread.Sleep(5000);
                    Console.WriteLine("--- Wait complete. About to call ShutdownWrite");

                    stream.ShutdownWrite();

                    Console.WriteLine("--- ShutdownWrite complete");

                    byte[] buffer = new byte[32 * 1024];
                    int length = 0;
                    while (true)
                    {
                        if (length == buffer.Length)
                        {
                            throw new Exception("Buffer too small");
                        }

                        int bytesRead = await stream.ReadAsync(new Memory<byte>(buffer).Slice(length));
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        Console.WriteLine($"--- Response: Read {bytesRead} bytes");

                        length += bytesRead;
                    }

                    Console.WriteLine($"--- Response: {Encoding.UTF8.GetString(buffer, 0, length)}");
                }
            }
        }
    }
}
