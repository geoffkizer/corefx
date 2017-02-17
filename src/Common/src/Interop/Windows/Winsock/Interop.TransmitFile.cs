// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Mswsock
    {
        [DllImport(Interop.Libraries.Mswsock, SetLastError = true)]
        internal static extern unsafe bool TransmitFile(
            SafeHandle socket,
            SafeHandle fileHandle,
            int numberOfBytesToWrite,
            int numberOfBytesPerSend,
            SafeNativeOverlapped overlapped,
            TransmitFileBuffers* buffers,
            TransmitFileOptions flags);

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct TransmitFileBuffers
        {
            internal byte* Head;
            internal int HeadLength;
            internal byte* Tail;
            internal int TailLength;
        }
    }
}
