using System.Diagnostics;

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

        public static unsafe bool TryCompleteAccept(SafeCloseSocket socket, byte[] socketAddress, ref int socketAddressLen, out IntPtr acceptedFd, out SocketError errorCode)
        {
            IntPtr fd = IntPtr.Zero;
            int sockAddrLen = socketAddressLen;
            SafeSocketHandle accepted;
            fixed (byte* rawSocketAddress = socketAddress)
            {
                try
                {
                    accepted = Socket.Accept_internal((SafeSocketHandle)socket, out var error, true);
                    errorCode = (SocketError)error;
                }
                catch (ObjectDisposedException)
                {
                    // The socket was closed, or is closing.
                    errorCode = SocketError.OperationAborted;
                    acceptedFd = (IntPtr)(-1);
                    return true;
                }
            }

            if (errorCode == SocketError.Success)
            {
                fd = socket.DangerousGetHandle();
                Debug.Assert(fd != (IntPtr)(-1), "Expected fd != -1");

                socketAddressLen = sockAddrLen;
                errorCode = SocketError.Success;
                acceptedFd = fd;

                return true;
            }

            acceptedFd = (IntPtr)(-1);
            if (errorCode != SocketError.InProgress && errorCode != SocketError.WouldBlock)
            {
                return true;
            }

            errorCode = SocketError.Success;
            return false;
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
