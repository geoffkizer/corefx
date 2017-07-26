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

        [Fact]
        public async void TestShutdownEffects_Close()
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

                    Console.WriteLine("TestShutdownEffects: About to Close");

                    client.Close();

                    Console.WriteLine("TestShutdownEffects: Closed");

                    int result = await receiveTask;

                    Console.WriteLine($"TestShutdownEffects: bytesReceived={result}");
                }
            }
        }

        [Fact]
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
}
