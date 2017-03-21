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
        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct Overlapped
        {
            // NativeOverlapped must be the first member, since we cast to/from it
            public NativeOverlapped _nativeOverlapped;
            public GCHandle _completionCallbackHandle;

            internal Action<int, int> GetCompletionCallback()
            {
                return (Action<int, int>)_completionCallbackHandle.Target;
            }

            internal static unsafe void IOCompletionCallback(uint errorCode, uint bytesTransferred, NativeOverlapped* nativeOverlapped)
            {
                Overlapped* overlapped = (Overlapped*)nativeOverlapped;
                Action<int, int> completionCallback = (Action<int, int>)overlapped->_completionCallbackHandle.Target;

                completionCallback((int)errorCode, (int)bytesTransferred);
            }
        }

        public unsafe struct OverlappedHandle : IDisposable
        {
            private Overlapped* _overlappedPtr;

            public unsafe OverlappedHandle(Action<int, int> completionCallback)
            {
                _overlappedPtr = (Overlapped*)Marshal.AllocHGlobal(sizeof(Overlapped));
                _overlappedPtr->_nativeOverlapped = default(NativeOverlapped);
                _overlappedPtr->_completionCallbackHandle = GCHandle.Alloc(completionCallback);
            }

            public void Dispose()
            {
                if (_overlappedPtr != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_overlappedPtr);
                    _overlappedPtr = null;
                }
            }

            internal NativeOverlapped* GetNativeOverlapped()
            {
                return (NativeOverlapped*)_overlappedPtr;
            }
        }

        [DllImport(Interop.Libraries.Kernel32, SetLastError = true)]
        private static extern unsafe bool BindIoCompletionCallback(
            IntPtr handle,
            IOCompletionCallback completionCallback,
            int flags);

        [DllImport(Interop.Libraries.Kernel32, SetLastError = true)]
        private static unsafe extern bool SetFileCompletionNotificationModes(
            IntPtr handle,
            Interop.Kernel32.FileCompletionNotificationModes flags);

        public static unsafe void BindToWin32ThreadPool(IntPtr socketHandle)
        {
            bool success = BindIoCompletionCallback(socketHandle, Overlapped.IOCompletionCallback, 0);
            if (!success)
            {
                throw new Exception($"BindIoCompletionCallback failed, GetLastError = { Marshal.GetLastWin32Error() }");
            }

            success = SetFileCompletionNotificationModes(socketHandle,
                Interop.Kernel32.FileCompletionNotificationModes.SkipCompletionPortOnSuccess |
                Interop.Kernel32.FileCompletionNotificationModes.SkipSetEventOnHandle);
            if (!success)
            {
                throw new Exception($"SetFileCompletionNotificationModes failed, GetLastError = { Marshal.GetLastWin32Error() }");
            }
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
            IntPtr socketHandle,
            byte* buffer,
            int bufferLength,
            out int bytesTransferred,
            ref SocketFlags socketFlags,
            OverlappedHandle overlapped)
        {
            WSABuffer wsaBuffer;
            wsaBuffer.Pointer = (IntPtr)buffer;
            wsaBuffer.Length = bufferLength;

            SocketError socketError = Interop.Winsock.WSARecv(socketHandle, &wsaBuffer, 1, out bytesTransferred, ref socketFlags, overlapped.GetNativeOverlapped(), IntPtr.Zero);
            if (socketError != SocketError.Success)
            {
                socketError = SocketPal.GetLastSocketError();
            }

            return socketError;
        }

        // Note, only single buffer supported for now
        public static unsafe SocketError Send(
            IntPtr socketHandle,
            byte* buffer,
            int bufferLength,
            out int bytesTransferred,
            SocketFlags socketFlags,
            OverlappedHandle overlapped)
        {
            WSABuffer wsaBuffer;
            wsaBuffer.Pointer = (IntPtr)buffer;
            wsaBuffer.Length = bufferLength;

            SocketError socketError = Interop.Winsock.WSASend(socketHandle, &wsaBuffer, 1, out bytesTransferred, socketFlags, overlapped.GetNativeOverlapped(), IntPtr.Zero);
            if (socketError != SocketError.Success)
            {
                socketError = SocketPal.GetLastSocketError();
            }

            return socketError;
        }
    }
}