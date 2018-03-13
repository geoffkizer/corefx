// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets
{
    internal partial class SafeCloseSocket :
#if DEBUG
        DebugSafeHandleMinusOneIsInvalid
#else
        SafeHandleMinusOneIsInvalid
#endif
    {
        private ThreadPoolBoundHandle _iocpBoundHandle;
        private bool _skipCompletionPortOnSuccess;
        private object _iocpBindingLock = new object();

        public void SetExposed() { /* nop */ }

        // TODO: These methods don't need to be on SafeCloseSocket and would probably be better on SimpleOverlapped itself,
        // Or really just not existing and have the code that uses it do it directly, as we're now doing in the SAEA case...
        internal unsafe NativeOverlapped* AllocateNativeOverlapped(IOCompletionCallback callback, object state, object pinData)
        {
            SimpleOverlapped simpleOverlapped = new SimpleOverlapped(callback, state, pinData);
            return simpleOverlapped.NativeOverlapped;
        }

        // TODO: Do not call in PreAllocated case...
        internal unsafe static void FreeNativeOverlapped(NativeOverlapped* nativeOverlapped)
        {
            Overlapped.Free(nativeOverlapped);
        }

        internal unsafe static object GetNativeOverlappedState(NativeOverlapped* nativeOverlapped)
        {
            SimpleOverlapped simpleOverlapped = (SimpleOverlapped)Overlapped.Unpack(nativeOverlapped);
            return simpleOverlapped.UserState;
        }

#if false
        public ThreadPoolBoundHandle IOCPBoundHandle
        {
            get
            {
                return _iocpBoundHandle;
            }
        }
#endif
        public bool IsBoundHandle() => GetThreadPoolBoundHandle() != null;
        public void BindHandle(bool trySkipCompletionPortOnSuccess)
        {
            GetOrAllocateThreadPoolBoundHandle(trySkipCompletionPortOnSuccess);
        }

        private ThreadPoolBoundHandle GetThreadPoolBoundHandle() => !_released ? _iocpBoundHandle : null;

        // Binds the Socket Win32 Handle to the ThreadPool's CompletionPort.
        private ThreadPoolBoundHandle GetOrAllocateThreadPoolBoundHandle(bool trySkipCompletionPortOnSuccess)
        {
            if (_released)
            {
                // Keep the exception message pointing at the external type.
                throw new ObjectDisposedException(typeof(Socket).FullName);
            }

            if (_iocpBoundHandle != null)
            {
                return _iocpBoundHandle;
            }

            lock (_iocpBindingLock)
            {
                ThreadPoolBoundHandle boundHandle = _iocpBoundHandle;

                if (boundHandle == null)
                {
                    // Bind the socket native _handle to the ThreadPool.
                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, "calling ThreadPool.BindHandle()");

                    try
                    {
                        // The handle (this) may have been already released:
                        // E.g.: The socket has been disposed in the main thread. A completion callback may
                        //       attempt starting another operation.
                        boundHandle = ThreadPoolBoundHandle.BindHandle(this);
                    }
                    catch (Exception exception) when (!ExceptionCheck.IsFatal(exception))
                    {
                        CloseAsIs();
                        throw;
                    }

                    // Try to disable completions for synchronous success, if requested
                    if (trySkipCompletionPortOnSuccess &&
                        CompletionPortHelper.SkipCompletionPortOnSuccess(boundHandle.Handle))
                    {
                        _skipCompletionPortOnSuccess = true;
                    }

                    // Don't set this until after we've configured the handle above (if we did)
                    Volatile.Write(ref _iocpBoundHandle, boundHandle);
                }

                return boundHandle;
            }
        }

        public bool SkipCompletionPortOnSuccess
        {
            get
            {
                Debug.Assert(IsBoundHandle());
                return _skipCompletionPortOnSuccess;
            }
        }

        internal static SafeCloseSocket CreateWSASocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
        {
            return CreateSocket(InnerSafeCloseSocket.CreateWSASocket(addressFamily, socketType, protocolType));
        }

        internal static SafeCloseSocket Accept(
            SafeCloseSocket socketHandle,
            byte[] socketAddress,
            ref int socketAddressSize)
        {
            return CreateSocket(InnerSafeCloseSocket.Accept(socketHandle, socketAddress, ref socketAddressSize));
        }

        private void InnerReleaseHandle()
        {
            // ThreadPoolBoundHandle doesn't actually do anything in Dispose, so skip this.
#if false
            // Keep m_IocpBoundHandle around after disposing it to allow freeing NativeOverlapped.
            // ThreadPoolBoundHandle allows FreeNativeOverlapped even after it has been disposed.
            if (_iocpBoundHandle != null)
            {
                _iocpBoundHandle.Dispose();
            }
#endif
        }

        internal sealed partial class InnerSafeCloseSocket : SafeHandleMinusOneIsInvalid
        {
            private SocketError InnerReleaseHandle()
            {
                SocketError errorCode;

                // If _blockable was set in BlockingRelease, it's safe to block here, which means
                // we can honor the linger options set on the socket.  It also means closesocket() might return WSAEWOULDBLOCK, in which
                // case we need to do some recovery.
                if (_blockable)
                {
                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"handle:{handle}, Following 'blockable' branch");
                    errorCode = Interop.Winsock.closesocket(handle);
#if DEBUG
                    _closeSocketHandle = handle;
                    _closeSocketResult = errorCode;
#endif
                    if (errorCode == SocketError.SocketError) errorCode = (SocketError)Marshal.GetLastWin32Error();

                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"handle:{handle}, closesocket()#1:{errorCode}");

                    // If it's not WSAEWOULDBLOCK, there's no more recourse - we either succeeded or failed.
                    if (errorCode != SocketError.WouldBlock)
                    {
                        return errorCode;
                    }

                    // The socket must be non-blocking with a linger timeout set.
                    // We have to set the socket to blocking.
                    int nonBlockCmd = 0;
                    errorCode = Interop.Winsock.ioctlsocket(
                        handle,
                        Interop.Winsock.IoctlSocketConstants.FIONBIO,
                        ref nonBlockCmd);
                    if (errorCode == SocketError.SocketError) errorCode = (SocketError)Marshal.GetLastWin32Error();

                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"handle:{handle}, ioctlsocket()#1:{errorCode}");

                    // If that succeeded, try again.
                    if (errorCode == SocketError.Success)
                    {
                        errorCode = Interop.Winsock.closesocket(handle);
#if DEBUG
                        _closeSocketHandle = handle;
                        _closeSocketResult = errorCode;
#endif
                        if (errorCode == SocketError.SocketError) errorCode = (SocketError)Marshal.GetLastWin32Error();
                        if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"handle:{handle}, closesocket#2():{errorCode}");

                        // If it's not WSAEWOULDBLOCK, there's no more recourse - we either succeeded or failed.
                        if (errorCode != SocketError.WouldBlock)
                        {
                            return errorCode;
                        }
                    }

                    // It failed.  Fall through to the regular abortive close.
                }

                // By default or if CloseAsIs() path failed, set linger timeout to zero to get an abortive close (RST).
                Interop.Winsock.Linger lingerStruct;
                lingerStruct.OnOff = 1;
                lingerStruct.Time = 0;

                errorCode = Interop.Winsock.setsockopt(
                    handle,
                    SocketOptionLevel.Socket,
                    SocketOptionName.Linger,
                    ref lingerStruct,
                    4);
#if DEBUG
                _closeSocketLinger = errorCode;
#endif
                if (errorCode == SocketError.SocketError) errorCode = (SocketError)Marshal.GetLastWin32Error();
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"handle:{handle}, setsockopt():{errorCode}");

                if (errorCode != SocketError.Success && errorCode != SocketError.InvalidArgument && errorCode != SocketError.ProtocolOption)
                {
                    // Too dangerous to try closesocket() - it might block!
                    return errorCode;
                }

                errorCode = Interop.Winsock.closesocket(handle);
#if DEBUG
                _closeSocketHandle = handle;
                _closeSocketResult = errorCode;
#endif
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"handle:{handle}, closesocket#3():{(errorCode == SocketError.SocketError ? (SocketError)Marshal.GetLastWin32Error() : errorCode)}");

                return errorCode;
            }

            internal static InnerSafeCloseSocket CreateWSASocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType)
            {
                InnerSafeCloseSocket result = Interop.Winsock.WSASocketW(addressFamily, socketType, protocolType, IntPtr.Zero, 0, Interop.Winsock.SocketConstructorFlags.WSA_FLAG_OVERLAPPED);
                if (result.IsInvalid)
                {
                    result.SetHandleAsInvalid();
                }
                return result;
            }

            internal static InnerSafeCloseSocket Accept(SafeCloseSocket socketHandle, byte[] socketAddress, ref int socketAddressSize)
            {
                InnerSafeCloseSocket result = Interop.Winsock.accept(socketHandle.DangerousGetHandle(), socketAddress, ref socketAddressSize);
                if (result.IsInvalid)
                {
                    result.SetHandleAsInvalid();
                }
                return result;
            }
        }
    }

    // Based on ThreadPoolBoundHandleOverlapped with the thread pool crap ripped out
    internal sealed class SimpleOverlapped : Overlapped
    {
        private readonly IOCompletionCallback _completionCallback;
        private readonly object _userState;
        private readonly unsafe NativeOverlapped* _nativeOverlapped;
        private uint _errorCode;
        private uint _numBytes;

        public unsafe SimpleOverlapped(IOCompletionCallback completionCallback, object state, object pinData)
        {
            // Base overlapped does not expose the completionCallback.  So just store it ourselves for now.
            _completionCallback = completionCallback;
            _userState = state;

            _nativeOverlapped = Pack(completionCallback, pinData);
        }

        public object UserState => _userState;
        public unsafe NativeOverlapped* NativeOverlapped => _nativeOverlapped;

        // TODO: This really only should be called once.  Currently we'll call it again when we retrieve the UserState.  Fix this.

        public static unsafe SimpleOverlapped FromNativeOverlapped(NativeOverlapped* nativeOverlapped)
        {
            var simpleOverlapped = (SimpleOverlapped)Unpack(nativeOverlapped);
            Debug.Assert(simpleOverlapped.NativeOverlapped == nativeOverlapped);
            return simpleOverlapped;
        }

        public unsafe void SetResult(uint errorCode, uint numBytes)
        {
            _errorCode = errorCode;
            _numBytes = numBytes;
        }

        public unsafe void InvokeCompletionCallback()
        {
            _completionCallback(_errorCode, _numBytes, _nativeOverlapped);
        }
    }
}
