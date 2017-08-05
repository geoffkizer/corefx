// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Threading;
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
    }

    public abstract class CloseTests<T> : SocketTestHelperBase<T> where T : SocketHelperBase, new()
    {
        // I really want something like the following tests:
        // (1) Shutdown with pending (recv, recv async, send, send async) + force nonblocking
        // (2) Close with same

        private Task<int> DoPendingReceive(Socket s)
        {
            Task<int> receiveTask = ReceiveAsync(s, new ArraySegment<byte>(new byte[1]));

            // Give the task some time to (hopefully) pend the receive
            Thread.Sleep(1000);

            return receiveTask;
        }

        private Task DoPendingSend(Socket s)
        {
            Task sendTask = Task.Run(async () =>
            {
                byte[] sendBuffer = new byte[16 * 1024];
                while (true)
                {
                    await SendAsync(s, new ArraySegment<byte>(sendBuffer));
                }
            });

            // Give the task some time to (hopefully) fill the send buffer and pend
            Thread.Sleep(1000);

            return sendTask;
        }

        // Helpers that prob go away
        private async Task ShowResult(string s, Task<int> t)
        {
            try
            {
                if (await Task.WhenAny(t, Task.Delay(1000)) == t)
                {
                    int result = await t;
                    Console.WriteLine($"{s}: bytes={result}");
                }
                else
                {
                    Console.WriteLine($"{s}: timed out");
                }
            }
            catch (SocketException e)
            {
                Console.WriteLine($"{s}: errorCode={e.SocketErrorCode}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"{s}: {e.GetType().FullName}");
            }
        }

        private async Task ShowResult(string s, Task t)
        {
            try
            {
                if (await Task.WhenAny(t, Task.Delay(1000)) == t)
                {
                    await t;
                    Console.WriteLine($"{s}: Completed");
                }
                else
                {
                    Console.WriteLine($"{s}: timed out");
                }
            }
            catch (SocketException e)
            {
                Console.WriteLine($"{s}: errorCode={e.SocketErrorCode}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"{s}: {e.GetType().FullName}");
            }
        }

        private async Task InvokeAndShowResult(string s, Func<Task<int>> f)
        {
            Task<int> t;
            try
            {
                t = f();
            }
            catch (SocketException e)
            {
                Console.WriteLine($"{s}: Synchronous SocketException, errorCode={e.SocketErrorCode}");
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"{s}: Synchronous {e.GetType().FullName}");
                return;
            }

            await ShowResult(s, t);
        }

        private async Task InvokeAndShowResult(string s, Func<Task> f)
        {
            Task t;
            try
            {
                t = f();
            }
            catch (SocketException e)
            {
                Console.WriteLine($"{s}: Synchronous SocketException, errorCode={e.SocketErrorCode}");
                return;
            }
            catch (Exception e)
            {
                Console.WriteLine($"{s}: Synchronous {e.GetType().FullName}");
                return;
            }

            await ShowResult(s, t);
        }

        [Fact]
        public async Task Close_WithPendingReceive()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    await serverTask;

                    Task<int> t = DoPendingReceive(client);

                    Console.WriteLine("Close_WithPendingReceive: About to Close");

                    bool closed = Task.Run(() => client.Close()).Wait(1000);
                    if (!closed)
                    {
                        Console.WriteLine("Close_WithPendingReceive: Close is hung");
                        return;
                    }
                    Console.WriteLine("Close_WithPendingReceive: Closed");

                    await ShowResult("Close_WithPendingReceive", t);
                }
            }
        }

        [Fact]
        public async Task Close_WithPendingSend()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    await serverTask;

                    Task t = DoPendingSend(client);

                    Console.WriteLine("Close_WithPendingSend: About to Close");

                    bool closed = Task.Run(() => client.Close()).Wait(1000);
                    if (!closed)
                    {
                        Console.WriteLine("Close_WithPendingSend: Close is hung");
                        return;
                    }
                    Console.WriteLine("Close_WithPendingSend: Closed");

                    await ShowResult("Close_WithPendingSend", t);
                }
            }
        }

        [Fact]
        public async Task ReceiveAfterClose()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task<Socket> serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    Socket accepted = await serverTask;

                    Task<int> receiveOne = ReceiveAsync(client, new ArraySegment<byte>(new byte[1]));
                    await SendAsync(accepted, new ArraySegment<byte>(new byte[1]));
                    Assert.Equal(1, await receiveOne);

                    Console.WriteLine("ReceiveAfterClose: About to Close");

                    bool closed = Task.Run(() => client.Close()).Wait(1000);
                    if (!closed)
                    {
                        Console.WriteLine("ReceiveAfterClose: Close is hung");
                        return;
                    }
                    Console.WriteLine("ReceiveAfterClose: Closed");

                    await InvokeAndShowResult("ReceiveAfterClose", () => ReceiveAsync(client, new ArraySegment<byte>(new byte[1])));
                }
            }
        }

        [Fact]
        public async Task SendAfterClose()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task<Socket> serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    Socket accepted = await serverTask;

                    Task sendOne = SendAsync(client, new ArraySegment<byte>(new byte[1]));
                    Assert.Equal(1, await ReceiveAsync(accepted, new ArraySegment<byte>(new byte[1])));
                    await sendOne;

                    Console.WriteLine("SendAfterClose: About to Close");

                    bool closed = Task.Run(() => client.Close()).Wait(1000);
                    if (!closed)
                    {
                        Console.WriteLine("SendAfterClose: Close is hung");
                        return;
                    }
                    Console.WriteLine("SendAfterClose: Closed");

                    await InvokeAndShowResult("SendAfterClose", () => SendAsync(client, new ArraySegment<byte>(new byte[1])));
                }
            }
        }

        [Fact]
        public async Task Shutdown_WithPendingReceive()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    await serverTask;

                    Task<int> t = DoPendingReceive(client);

                    Console.WriteLine("Shutdown_WithPendingReceive: About to Shutdown");
                    client.Shutdown(SocketShutdown.Receive);
                    Console.WriteLine("Shutdown_WithPendingReceive: Shutdown");

                    await ShowResult("Shutdown_WithPendingReceive", t);
                }
            }
        }

        [Fact]
        public async Task Shutdown_WithPendingSend()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    await serverTask;

                    Task t = DoPendingSend(client);

                    Console.WriteLine("Shutdown_WithPendingSend: About to Shutdown");
                    client.Shutdown(SocketShutdown.Send);
                    Console.WriteLine("Shutdown_WithPendingSend: Shutdown");

                    await ShowResult("Shutdown_WithPendingSend", t);
                }
            }
        }

        [Fact]
        public async Task ReceiveAfterShutdown()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task<Socket> serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    Socket accepted = await serverTask;

                    Task<int> receiveOne = ReceiveAsync(client, new ArraySegment<byte>(new byte[1]));
                    await SendAsync(accepted, new ArraySegment<byte>(new byte[1]));
                    Assert.Equal(1, await receiveOne);

                    Console.WriteLine("ReceiveAfterShutdown: About to Shutdown");
                    client.Shutdown(SocketShutdown.Receive);
                    Console.WriteLine("ReceiveAfterShutdown: Shutdown completed");

                    await InvokeAndShowResult("ReceiveAfterShutdown", () => ReceiveAsync(client, new ArraySegment<byte>(new byte[1])));
                }
            }
        }

        [Fact]
        public async Task SendAfterShutdown()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task<Socket> serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    Socket accepted = await serverTask;

                    Task sendOne = SendAsync(client, new ArraySegment<byte>(new byte[1]));
                    Assert.Equal(1, await ReceiveAsync(accepted, new ArraySegment<byte>(new byte[1])));
                    await sendOne;

                    Console.WriteLine("SendAfterShutdown: About to Shutdown");
                    client.Shutdown(SocketShutdown.Send);
                    Console.WriteLine("SendAfterShutdown: Shutdown completed");

                    await InvokeAndShowResult("SendAfterClose", () => SendAsync(client, new ArraySegment<byte>(new byte[1])));
                }
            }
        }

        [Fact]
        public async Task ShutdownReceiveTwice()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task<Socket> serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    Socket accepted = await serverTask;

                    Task<int> receiveOne = ReceiveAsync(client, new ArraySegment<byte>(new byte[1]));
                    await SendAsync(accepted, new ArraySegment<byte>(new byte[1]));
                    Assert.Equal(1, await receiveOne);

                    client.Shutdown(SocketShutdown.Receive);

                    Thread.Sleep(1000);

                    // Second shutdown should be a no-op and not throw
                    client.Shutdown(SocketShutdown.Receive);
                }
            }
        }

        [Fact]
        public async Task ShutdownSendTwice()
        {
            using (var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                server.BindToAnonymousPort(IPAddress.Loopback);
                server.Listen(1);

                Task<Socket> serverTask = AcceptAsync(server);

                using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    await ConnectAsync(client, server.LocalEndPoint);
                    Socket accepted = await serverTask;

                    Task sendOne = SendAsync(client, new ArraySegment<byte>(new byte[1]));
                    Assert.Equal(1, await ReceiveAsync(accepted, new ArraySegment<byte>(new byte[1])));
                    await sendOne;

                    client.Shutdown(SocketShutdown.Send);

                    Thread.Sleep(1000);

                    // Second shutdown should be a no-op and not throw
                    client.Shutdown(SocketShutdown.Send);
                }
            }
        }
    }

    public sealed class CloseTestsSync : CloseTests<SocketHelperSync> { }
    public sealed class CloseTestsSyncForceNonBlocking : CloseTests<SocketHelperSyncForceNonBlocking> { }
    public sealed class CloseTestsApm : CloseTests<SocketHelperApm> { }
    public sealed class CloseTestsTask : CloseTests<SocketHelperTask> { }
    public sealed class CloseTestsEap : CloseTests<SocketHelperEap> { }
}
