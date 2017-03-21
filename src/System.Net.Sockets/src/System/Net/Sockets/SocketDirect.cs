using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets
{
    [CLSCompliant(false)]
    public static class SocketDirect
    {
        public unsafe struct OverlappedHandle : IDisposable
        {
            private PreAllocatedOverlapped _preallocatedOverlapped;

            public OverlappedHandle(ThreadPoolBoundHandle boundHandle, Action<int, int> completionCallback)
            {
                _preallocatedOverlapped = new PreAllocatedOverlapped(
                    (errorCode, bytesTransferred, nativeOverlapped) =>
                    {
                        boundHandle.FreeNativeOverlapped(nativeOverlapped);
                        completionCallback((int)errorCode, (int)bytesTransferred);
                    },
                    null, null);
            }

            public PreAllocatedOverlapped PreAllocatedOverlapped => _preallocatedOverlapped;

            public void Dispose()
            {
                if (_preallocatedOverlapped != null)
                {
                    _preallocatedOverlapped.Dispose();
                    _preallocatedOverlapped = null;
                }
            }
        }

        public static ThreadPoolBoundHandle BindToClrThreadPool(Socket s)
        {
            return s.GetOrAllocateThreadPoolBoundHandle();
        }

        [DllImport(Interop.Libraries.Ws2_32, SetLastError = true)]
        private static extern SocketError setsockopt(
            IntPtr socketHandle,
            SocketOptionLevel optionLevel,
            SocketOptionName optionName,
            ref int optionValue,
            int optionLength);

        public static void SetNoDelay(IntPtr socketHandle)
        {
            int nodelay = 1;
            setsockopt(socketHandle, SocketOptionLevel.Tcp, SocketOptionName.NoDelay, ref nodelay, 4);
        }

        // Note, only single buffer supported for now
        public static unsafe SocketError Receive(
            ThreadPoolBoundHandle socketHandle,
            byte* buffer,
            int bufferLength,
            out int bytesTransferred,
            ref SocketFlags socketFlags,
            OverlappedHandle overlapped)
        {
            WSABuffer wsaBuffer;
            wsaBuffer.Pointer = (IntPtr)buffer;
            wsaBuffer.Length = bufferLength;

            NativeOverlapped* nativeOverlapped = socketHandle.AllocateNativeOverlapped(overlapped.PreAllocatedOverlapped);
            SocketError socketError = Interop.Winsock.WSARecv(socketHandle.Handle.DangerousGetHandle(), &wsaBuffer, 1, out bytesTransferred, ref socketFlags, nativeOverlapped, IntPtr.Zero);
            if (socketError != SocketError.Success)
            {
                socketError = SocketPal.GetLastSocketError();
            }

            if (socketError != SocketError.IOPending)
            {
                socketHandle.FreeNativeOverlapped(nativeOverlapped);
            }

            return socketError;
        }

        // Note, only single buffer supported for now
        public static unsafe SocketError Send(
            ThreadPoolBoundHandle socketHandle,
            byte* buffer,
            int bufferLength,
            out int bytesTransferred,
            SocketFlags socketFlags,
            OverlappedHandle overlapped)
        {
            WSABuffer wsaBuffer;
            wsaBuffer.Pointer = (IntPtr)buffer;
            wsaBuffer.Length = bufferLength;

            NativeOverlapped* nativeOverlapped = socketHandle.AllocateNativeOverlapped(overlapped.PreAllocatedOverlapped);
            SocketError socketError = Interop.Winsock.WSASend(socketHandle.Handle.DangerousGetHandle(), &wsaBuffer, 1, out bytesTransferred, socketFlags, nativeOverlapped, IntPtr.Zero);
            if (socketError != SocketError.Success)
            {
                socketError = SocketPal.GetLastSocketError();
            }

            if (socketError != SocketError.IOPending)
            {
                socketHandle.FreeNativeOverlapped(nativeOverlapped);
            }

            return socketError;
        }
    }
}