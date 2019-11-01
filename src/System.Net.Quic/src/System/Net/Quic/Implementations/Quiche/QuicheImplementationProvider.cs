// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Security;
using System.Runtime.InteropServices;

namespace System.Net.Quic.Implementations.Quiche
{
    internal sealed class QuicheImplementationProvider : QuicImplementationProvider
    {
        internal override QuicListenerProvider CreateListener(IPEndPoint listenEndPoint, SslServerAuthenticationOptions sslServerAuthenticationOptions)
        {
            throw new NotImplementedException();
            //return new QuicheListener(listenEndPoint, sslServerAuthenticationOptions);
        }

        internal override QuicConnectionProvider CreateConnection(IPEndPoint remoteEndPoint, SslClientAuthenticationOptions sslClientAuthenticationOptions, IPEndPoint localEndPoint)
        {
            return new QuicheConnection(remoteEndPoint, sslClientAuthenticationOptions, localEndPoint);
        }

        internal static readonly bool s_enableDebugLogging = Environment.GetEnvironmentVariable("QUICHEDEBUG") != null;

        static QuicheImplementationProvider()
        {
            if (s_enableDebugLogging)
            {
                RegisterDebugLoggerCallback(s => Console.WriteLine($"QUICHEDEBUG: {s}"));
            }
        }

        internal static void RegisterDebugLoggerCallback(Action<string> callback)
        {
            NativeMethods.DebugLoggingCallback nativeCallback = (IntPtr str, IntPtr state) =>
            {
                string message = Marshal.PtrToStringUTF8(str);
                callback(message);
            };

            NativeMethods.quiche_enable_debug_logging(nativeCallback, IntPtr.Zero);
        }

    }
}
