using System.Diagnostics;

namespace System.Net.Sockets
{
	static class SocketPal
	{
		public static unsafe bool TryStartConnect (SafeSocketHandle socket, byte[] socketAddress, int socketAddressLen, out SocketError errorCode)
		{
			Debug.Assert (socketAddress != null, "Expected non-null socketAddress");
			Debug.Assert (socketAddressLen > 0, $"Unexpected socketAddressLen: {socketAddressLen}");

			if (socket.IsDisconnected) {
				errorCode = SocketError.IsConnected;
				return true;
			}

			var sa = new SocketAddress (socketAddress, socketAddressLen);

			Socket.Connect_internal (socket, sa, out var error, false);

			if (error == 0) {
				errorCode = SocketError.Success;
				return true;
			}

			if (error != (int) SocketError.InProgress && error != (int) SocketError.WouldBlock) {
				errorCode = (SocketError)error;
				return true;
			}

			errorCode = SocketError.Success;
			return false;
		}		
	}
}
