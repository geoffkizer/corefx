// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Net.Sockets.Tests;
using System.Net.Test.Common;

using System.Threading.Tasks;
using System.Text.Encodings;
using System.Text;

using Xunit;
using Xunit.Abstractions;

namespace System.Net.Sockets.Performance.Tests
{
    [Trait("Perf", "true")]
    public class SocketPerformanceAsyncTests
    {
        private readonly ITestOutputHelper _log;
        private readonly int _iterations = 1;

        public SocketPerformanceAsyncTests(ITestOutputHelper output)
        {
            _log = TestLogging.GetInstance();

            string env = Environment.GetEnvironmentVariable("SOCKETSTRESS_ITERATIONS");
            if (env != null)
            {
                _iterations = int.Parse(env);
            }
        }

#if false
        [ActiveIssue(13349, TestPlatforms.OSX)]
        [OuterLoop]
        [Fact]
        public void SocketPerformance_MultipleSocketClientAsync_LocalHostServerAsync()
        {
            SocketImplementationType serverType = SocketImplementationType.Async;
            SocketImplementationType clientType = SocketImplementationType.Async;
            int iterations = 200 * _iterations;
            int bufferSize = 256;
            int socket_instances = 200;

            var test = new SocketPerformanceTests(_log);

            // Run in Stress mode no expected time to complete.
            test.ClientServerTest(
                serverType,
                clientType,
                iterations,
                bufferSize,
                socket_instances);
        }
#endif

        [Fact]
        public async void Test1()
        {
            Socket listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listenSocket.Listen(100);

            EndPoint ep = listenSocket.LocalEndPoint;

            Task t = Task.Run(async () => {
                Socket serverSocket = await listenSocket.AcceptAsync();
                ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[4096]);

                try
                {
                    int bytesReceived = await serverSocket.ReceiveAsync(buffer, SocketFlags.None);
                    Console.WriteLine("Received {0} bytes", bytesReceived);

                    serverSocket.Close();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Exception on server thread: {e}");
                }
            });

            Socket clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

            await clientSocket.ConnectAsync(ep);

            await clientSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("Hello world")), SocketFlags.None);

            await t;
        }



    }
}
