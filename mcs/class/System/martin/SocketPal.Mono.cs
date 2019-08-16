using System.Diagnostics;

namespace System.Net.Sockets
{
    static class SocketPal
    {
        public static SocketError CreateSocket(AddressFamily addressFamily, SocketType socketType, ProtocolType protocolType, out SafeSocketHandle socket)
        {
            return SafeCloseSocket.CreateSocket(addressFamily, socketType, protocolType, out socket);
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

        public static SocketError Connect(SafeSocketHandle handle, byte[] socketAddress, int socketAddressLen)
        {
            SocketError errorCode;
            bool completed = TryStartConnect(handle, socketAddress, socketAddressLen, !handle.IsNonBlocking, out errorCode);
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

        public static bool TryStartConnect(SafeSocketHandle socket, byte[] socketAddress, int socketAddressLen, out SocketError errorCode)
        {
            return TryStartConnect(socket, socketAddress, socketAddressLen, false, out errorCode);
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

        public static bool TryCompleteConnect(SafeSocketHandle socket, int socketAddressLen, out SocketError errorCode)
        {
            try
            {
                errorCode = (SocketError)Socket.GetSocketOption(socket, SocketOptionLevel.Socket, SocketOptionName.Error);
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
