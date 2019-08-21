using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace System.Net.Sockets
{
    static class SocketPal
    {
#region CoreFX Code

        public static SocketError GetSocketErrorForErrorCode(Interop.Error errorCode)
        {
            return SocketErrorPal.GetSocketErrorForNativeError(errorCode);
        }

        public static SocketError SetBlocking(SafeCloseSocket handle, bool shouldBlock, out bool willBlock)
        {
            handle.IsNonBlocking = !shouldBlock;
            willBlock = shouldBlock;
            return SocketError.Success;
        }

        public static SocketError Accept(SafeCloseSocket handle, byte[] buffer, ref int nameLen, out SafeCloseSocket socket)
        {
            return SafeCloseSocket.Accept(handle, buffer, ref nameLen, out socket);
        }

        public static SocketError Connect(SafeCloseSocket handle, byte[] socketAddress, int socketAddressLen)
        {
            if (!handle.IsNonBlocking)
            {
                return handle.AsyncContext.Connect(socketAddress, socketAddressLen);
            }

            SocketError errorCode;
            bool completed = TryStartConnect(handle, socketAddress, socketAddressLen, out errorCode);
            if (completed)
            {
                handle.RegisterConnectResult(errorCode);
                return errorCode;
            }
            else
            {
                return SocketError.WouldBlock;
            }
        }

        public static unsafe bool TryCompleteAccept(SafeCloseSocket socket, byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd, out SocketError errorCode)
        {
            IntPtr fd = IntPtr.Zero;
            Interop.Error errno;
            int sockAddrLen = socketAddressLen;
            fixed (byte* rawSocketAddress = socketAddress)
            {
                try
                {
                    errno = Interop.Sys.Accept(socket, rawSocketAddress, &sockAddrLen, &fd);
                }
                catch (ObjectDisposedException)
                {
                    // The socket was closed, or is closing.
                    errorCode = SocketError.OperationAborted;
                    acceptedFd = (IntPtr)(-1);
                    return true;
                }
            }

            if (errno == Interop.Error.SUCCESS)
            {
                Debug.Assert(fd != (IntPtr)(-1), "Expected fd != -1");

                socketAddressLen = sockAddrLen;
                errorCode = SocketError.Success;
                acceptedFd = fd;

                return true;
            }

            acceptedFd = (IntPtr)(-1);
            if (errno != Interop.Error.EAGAIN && errno != Interop.Error.EWOULDBLOCK)
            {
                errorCode = GetSocketErrorForErrorCode(errno);
                return true;
            }

            errorCode = SocketError.Success;
            return false;
        }

        private static unsafe int Receive(SafeCloseSocket socket, SocketFlags flags, Span<byte> buffer, byte[] socketAddress, ref int socketAddressLen, out SocketFlags receivedFlags, out Interop.Error errno)
        {
            Debug.Assert(socketAddress != null || socketAddressLen == 0, $"Unexpected values: socketAddress={socketAddress}, socketAddressLen={socketAddressLen}");

            long received = 0;
            int sockAddrLen = socketAddress != null ? socketAddressLen : 0;

            fixed (byte* sockAddr = socketAddress)
            fixed (byte* b = &MemoryMarshal.GetReference(buffer))
            {
                var iov = new Interop.Sys.IOVector {
                    Base = b,
                    Count = (UIntPtr)buffer.Length
                };

                var messageHeader = new Interop.Sys.MessageHeader {
                    SocketAddress = sockAddr,
                    SocketAddressLen = sockAddrLen,
                    IOVectors = &iov,
                    IOVectorCount = 1
                };

                errno = Interop.Sys.ReceiveMessage(
#if MONO
                    socket,
#else
                    socket.DangerousGetHandle(), // to minimize chances of handle recycling from misuse, this should use DangerousAddRef/Release, but it adds too much overhead
#endif
                    &messageHeader,
                    flags,
                    &received);
                GC.KeepAlive(socket); // small extra safe guard against handle getting collected/finalized while P/Invoke in progress

                receivedFlags = messageHeader.Flags;
                sockAddrLen = messageHeader.SocketAddressLen;
            }

            if (errno != Interop.Error.SUCCESS)
            {
                return -1;
            }

            socketAddressLen = sockAddrLen;
            return checked((int)received);
        }

        private static unsafe int Receive(SafeCloseSocket socket, SocketFlags flags, IList<ArraySegment<byte>> buffers, byte[] socketAddress, ref int socketAddressLen, out SocketFlags receivedFlags, out Interop.Error errno)
        {
            int available = 0;
            errno = Interop.Sys.GetBytesAvailable(socket, &available);
            if (errno != Interop.Error.SUCCESS)
            {
                receivedFlags = 0;
                return -1;
            }
            if (available == 0)
            {
                // Don't truncate iovecs.
                available = int.MaxValue;
            }

            // Pin buffers and set up iovecs.
            int maxBuffers = buffers.Count;
            var handles = new GCHandle[maxBuffers];
            var iovecs = new Interop.Sys.IOVector[maxBuffers];

            int sockAddrLen = 0;
            if (socketAddress != null)
            {
                sockAddrLen = socketAddressLen;
            }

            long received = 0;
            int toReceive = 0, iovCount = maxBuffers;
            try
            {
                for (int i = 0; i < maxBuffers; i++)
                {
                    ArraySegment<byte> buffer = buffers[i];
                    RangeValidationHelpers.ValidateSegment(buffer);

                    handles[i] = GCHandle.Alloc(buffer.Array, GCHandleType.Pinned);
                    iovecs[i].Base = &((byte*)handles[i].AddrOfPinnedObject())[buffer.Offset];

                    int space = buffer.Count;
                    toReceive += space;
                    if (toReceive >= available)
                    {
                        iovecs[i].Count = (UIntPtr)(space - (toReceive - available));
                        toReceive = available;
                        iovCount = i + 1;
                        for (int j = i + 1; j < maxBuffers; j++)
                        {
                            // We're not going to use these extra buffers, but validate their args
                            // to alert the dev to a mistake and to be consistent with Windows.
                            RangeValidationHelpers.ValidateSegment(buffers[j]);
                        }
                        break;
                    }

                    iovecs[i].Count = (UIntPtr)space;
                }

                // Make the call.
                fixed (byte* sockAddr = socketAddress)
                fixed (Interop.Sys.IOVector* iov = iovecs)
                {
                    var messageHeader = new Interop.Sys.MessageHeader {
                        SocketAddress = sockAddr,
                        SocketAddressLen = sockAddrLen,
                        IOVectors = iov,
                        IOVectorCount = iovCount
                    };

                    errno = Interop.Sys.ReceiveMessage(
#if MONO
                        socket,
#else
                        socket.DangerousGetHandle(), // to minimize chances of handle recycling from misuse, this should use DangerousAddRef/Release, but it adds too much overhead
#endif
                        &messageHeader,
                        flags,
                        &received);
                    GC.KeepAlive(socket); // small extra safe guard against handle getting collected/finalized while P/Invoke in progress

                    receivedFlags = messageHeader.Flags;
                    sockAddrLen = messageHeader.SocketAddressLen;
                }
            }
            finally
            {
                // Free GC handles.
                for (int i = 0; i < iovCount; i++)
                {
                    if (handles[i].IsAllocated)
                    {
                        handles[i].Free();
                    }
                }
            }

            if (errno != Interop.Error.SUCCESS)
            {
                return -1;
            }

            socketAddressLen = sockAddrLen;
            return checked((int)received);
        }

#endregion

        public static SocketError CreateSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, out SafeSocketHandle socket)
        {
            return SafeCloseSocket.CreateSocket(addressFamily, socketType, protocolType, out socket);
        }

        public static unsafe SocketError GetSockName(SafeSocketHandle handle, byte[] buffer, ref int nameLen)
        {
            fixed (byte* rawBuffer = buffer)
            {
                return Socket.GetSockName_internal(handle, rawBuffer, ref nameLen);
            }
        }

        public static unsafe SocketError GetPeerName(SafeSocketHandle handle, byte[] buffer, ref int nameLen)
        {
            fixed (byte* rawBuffer = buffer)
            {
                return Socket.GetPeerName_internal(handle, rawBuffer, ref nameLen);
            }
        }

        public static unsafe SocketError Bind(SafeCloseSocket handle, ProtocolType socketProtocolType, byte[] buffer, int nameLen)
        {
            var sa = new SocketAddress(buffer, nameLen);

            Socket.Bind_internal((SafeSocketHandle)handle, sa, out var error);
            return (SocketError)error;
        }

        public static SocketError Listen(SafeCloseSocket handle, int backlog)
        {
            Socket.Listen_internal((SafeSocketHandle)handle, backlog, out var error);
            return (SocketError)error;
        }

        public static SocketError ConnectAsync(Socket socket, SafeCloseSocket handle, byte[] socketAddress, int socketAddressLen, ConnectOverlappedAsyncResult asyncResult)
        {
            SocketError socketError = handle.AsyncContext.ConnectAsync(socketAddress, socketAddressLen, asyncResult.CompletionCallback);
            if (socketError == SocketError.Success)
            {
                asyncResult.CompletionCallback(SocketError.Success);
            }
            return socketError;
        }

        public static bool TryStartConnect(SafeCloseSocket socket, byte[] socketAddress, int socketAddressLen, out SocketError errorCode)
        {
            return TryStartConnect((SafeSocketHandle)socket, socketAddress, socketAddressLen, false, out errorCode);
        }

        private static bool TryStartConnect(SafeSocketHandle socket, byte[] socketAddress, int socketAddressLen, bool block, out SocketError errorCode)
        {
            Debug.Assert(socketAddress != null, "Expected non-null socketAddress");
            Debug.Assert(socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");

            if (socket.IsDisconnected)
            {
                errorCode = SocketError.IsConnected;
                return true;
            }

            var sa = new SocketAddress(socketAddress, socketAddressLen);

            Socket.Connect_internal(socket, sa, out var error, block);

            if (error == 0)
            {
                errorCode = SocketError.Success;
                return true;
            }

            if (error != (int) SocketError.InProgress && error != (int) SocketError.WouldBlock)
            {
                errorCode = (SocketError)error;
                return true;
            }

            errorCode = SocketError.Success;
            return false;
        }

        public static bool TryCompleteConnect(SafeCloseSocket socket, int socketAddressLen, out SocketError errorCode)
        {
            try
            {
                errorCode = (SocketError)Socket.GetSocketOption((SafeSocketHandle)socket, SocketOptionLevel.Socket, SocketOptionName.Error);
            }
            catch (ObjectDisposedException)
            {
                // The socket was closed, or is closing.
                errorCode = SocketError.OperationAborted;
                return true;
            }

            if (errorCode == SocketError.Success)
            {
                return true;
            }

            if (errorCode == SocketError.InProgress)
            {
                errorCode = SocketError.Success;
                return false;
            }

            return true;
        }

    }
}
