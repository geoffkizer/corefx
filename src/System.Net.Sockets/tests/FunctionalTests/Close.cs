// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading.Tasks;

using Xunit;

namespace System.Net.Sockets.Tests
{
    public class CloseTests
    {
        [Fact]
        public static void Close()
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Close();
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(1)]
        public static void Close_Timeout(int timeout)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                s.Close(timeout);
            }
        }

        [Fact]
        public static void Close_BadTimeout_Throws()
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => s.Close(-2));
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
//        [ActiveIssue(22564, TestPlatforms.AnyUnix)]
        public async void Close_WithPendingSyncReceive(bool forceNonBlocking)
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task serverTask = server.AcceptAsync();

                EndPoint clientEndpoint = server.LocalEndPoint;
                using (var client = new Socket(clientEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    client.Connect(clientEndpoint);
                    await serverTask;

                    // Hang a blocking receive
                    Task receiveTask = Task.Run(() =>
                    {
                        try
                        {
                            client.Receive(new byte[1]);
                            Assert.True(false); // Should never reach this
                        }
                        catch (SocketException e)
                        {
                            Assert.Equal(SocketError.ConnectionAborted, e.SocketErrorCode);
                        }
                    });

                    // Delay to try to ensure the Receive is pending before we close the socket
                    await Task.Delay(500);

                    Console.WriteLine("About to Close");

                    client.Close();

                    Console.WriteLine("Close returned, about to Wait");

                    bool completed = receiveTask.Wait(TestSettings.PassingTestTimeout);
                    Assert.True(completed);
                }
            }
        }

        [Fact]
        public async void Close_WithPendingAsyncReceive()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task serverTask = server.AcceptAsync();

                EndPoint clientEndpoint = server.LocalEndPoint;
                using (var client = new Socket(clientEndpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
                {
                    client.Connect(clientEndpoint);
                    await serverTask;

                    // Hang an async receive
                    Task receiveTask = Task.Run(async () =>
                    {
                        try
                        {
                            await client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), SocketFlags.None);
                            Assert.True(false); // Should never reach this
                        }
                        catch (SocketException e)
                        {
                            Assert.Equal(SocketError.OperationAborted, e.SocketErrorCode);
                        }
                    });

                    // Delay to try to ensure the Receive is pending before we close the socket
                    await Task.Delay(500);

                    client.Close();

                    bool completed = receiveTask.Wait(TestSettings.PassingTestTimeout);
                    Assert.True(completed);
                }
            }
        }
    }
}
