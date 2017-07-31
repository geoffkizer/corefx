// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace System.Net.Sockets.Tests
{
    public class SetNonBlockingTest
    {
        //        [Fact]
        public async void SetNonBlocking()
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

                    DateTime start = DateTime.UtcNow;

                    // Hang a blocking receive
                    Task receiveTask = Task.Run(() =>
                    {
                        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId:X}: About to sync Receive");

                        try
                        {
                            client.Receive(new byte[1]);
                        }

                        catch (Exception e)
                        {
                            Console.Write($"{Thread.CurrentThread.ManagedThreadId:X}: Caught exception, time={DateTime.UtcNow - start}, e={e}");
                        }

                        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId:X}: Receive completed");
                    });

                    // Wait a bit and then change to nonblocking
                    Thread.Sleep(5000);

                    Console.WriteLine("About to set nonblocking");

                    client.Blocking = false;

                    bool completed = receiveTask.Wait(5000);
                    Console.WriteLine($"Wait complete, time={DateTime.UtcNow - start}, completed={completed}");
                }
            }
        }

        //        [Fact]
        public async void SetTimeout()
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

                    DateTime start = DateTime.UtcNow;

                    // Hang a blocking receive
                    Task receiveTask = Task.Run(() =>
                    {
                        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId:X}: About to sync Receive");

                        try
                        {
                            client.Receive(new byte[1]);
                        }

                        catch (Exception e)
                        {
                            Console.Write($"{Thread.CurrentThread.ManagedThreadId:X}: Caught exception, time={DateTime.UtcNow - start}, e={e}");
                        }

                        Console.WriteLine($"{Thread.CurrentThread.ManagedThreadId:X}: Receive completed");
                    });

                    // Wait a bit and then change to nonblocking
                    Thread.Sleep(5000);

                    Console.WriteLine("About to set timeout");

                    client.ReceiveTimeout = 1000;

                    bool completed = receiveTask.Wait(5000);
                    Console.WriteLine($"Wait complete, time={DateTime.UtcNow - start}, completed={completed}");
                }
            }
        }

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
//        [InlineData(true, false, true)]
//        [InlineData(false, true, true)]
//        [InlineData(true, true, true)]
//        [InlineData(false, false, true)]
        public async void TestShutdownEffects_Async(bool doReceive, bool doSend, bool doShutdown)
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

                    // Hang async ops
                    Task<int> receiveTask = null;
                    if (doReceive)
                    {
                        receiveTask = client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), SocketFlags.None);
                    }

                    Task<int> sendTask = null;
                    if (doSend)
                    {
                        byte[] sendBuffer = new byte[16 * 1024];
                        while (true)
                        {
                            sendTask = client.SendAsync(new ArraySegment<byte>(sendBuffer), SocketFlags.None);
                            if (!sendTask.IsCompleted)
                            {
                                break;
                            }
                            await sendTask;
                        }
                    }

                    Thread.Sleep(1000);

                    if (doShutdown)
                    {
                        Console.WriteLine("TestShutdownEffects: About to Shutdown");
                        client.Shutdown(SocketShutdown.Both);
                        Console.WriteLine("TestShutdownEffects: Shutdown complete");
                    }
                    else
                    {
                        Console.WriteLine("TestShutdownEffects: About to Close");
                        client.Close();
                        Console.WriteLine("TestShutdownEffects: Closed");
                    }

                    if (doReceive)
                    {
                        try
                        {
                            int result = await receiveTask;
                            Console.WriteLine($"TestShutdownEffects Receive: bytes={result}");
                        }
                        catch (SocketException e)
                        {
                            Console.WriteLine($"TestShutdownEffects Receive: errorCode={e.SocketErrorCode}");
                        }
                    }

                    if (doSend)
                    {
                        try
                        {
                            int result = await sendTask;
                            Console.WriteLine($"TestShutdownEffects Send: bytes={result}");
                        }
                        catch (SocketException e)
                        {
                            Console.WriteLine($"TestShutdownEffects Send: errorCode={e.SocketErrorCode}");
                        }
                    }
                }
            }
        }

        // I really want something like the following tests:
        // (1) Shutdown with pending (recv, recv async, send, send async) + force nonblocking
        // (2) Close with same

        [Theory]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
//        [InlineData(true, false, true)]
//        [InlineData(false, true, true)]
//        [InlineData(true, true, true)]
//        [InlineData(false, false, true)]
        public async void TestShutdownEffects_Sync(bool doReceive, bool doSend, bool doShutdown)
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

                    // Hang sync ops
                    Task<int> receiveTask = null;
                    if (doReceive)
                    {
                        receiveTask = Task.Run(() => client.Receive(new byte[1]));
                    }

                    Task sendTask = null;
                    if (doSend)
                    {
                        sendTask = Task.Run(() =>
                        {
                            byte[] sendBuffer = new byte[16 * 1024];
                            while (true)
                            {
                                client.Send(sendBuffer);
                            }
                        });
                    }

                    Thread.Sleep(1000);

                    if (doShutdown)
                    {
                        Console.WriteLine("TestShutdownEffects: About to Shutdown");
                        client.Shutdown(SocketShutdown.Both);
                        Console.WriteLine("TestShutdownEffects: Shutdown complete");
                    }
                    else
                    {
                        Console.WriteLine("TestShutdownEffects: About to Close");
                        client.Close();
                        Console.WriteLine("TestShutdownEffects: Closed");
                    }

                    if (doReceive)
                    {
                        try
                        {
                            int result = await receiveTask;
                            Console.WriteLine($"TestShutdownEffects Receive: bytes={result}");
                        }
                        catch (SocketException e)
                        {
                            Console.WriteLine($"TestShutdownEffects Receive: errorCode={e.SocketErrorCode}");
                        }
                    }

                    if (doSend)
                    {
                        try
                        {
                            await sendTask;
                            Console.WriteLine($"TestShutdownEffects Send completed");
                        }
                        catch (SocketException e)
                        {
                            Console.WriteLine($"TestShutdownEffects Send: errorCode={e.SocketErrorCode}");
                        }
                    }
                }
            }
        }

        //        [Fact]
        public async void TestShutdownEffects_Shutdown()
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
                    Task<int> receiveTask = client.ReceiveAsync(new ArraySegment<byte>(new byte[1]), SocketFlags.None);

                    // Wait a bit and then change to nonblocking
                    Thread.Sleep(1000);

                    Console.WriteLine("TestShutdownEffects: About to Shutdown");

                    client.Shutdown(SocketShutdown.Both);

                    Console.WriteLine("TestShutdownEffects: Shutdown returned");

                    int result = await receiveTask;

                    Console.WriteLine($"TestShutdownEffects: bytesReceived={result}");
                }
            }
        }
    }

    public abstract class ShutdownEffects<T> : SocketTestHelperBase<T> where T : SocketHelperBase, new()
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
    }

    public sealed class ShutdownEffectsSync : ShutdownEffects<SocketHelperSync> { }
    public sealed class ShutdownEffectsSyncForceNonBlocking : ShutdownEffects<SocketHelperSyncForceNonBlocking> { }
    public sealed class ShutdownEffectsApm : ShutdownEffects<SocketHelperApm> { }
    public sealed class ShutdownEffectsTask : ShutdownEffects<SocketHelperTask> { }
    public sealed class ShutdownEffectsEap : ShutdownEffects<SocketHelperEap> { }
}
