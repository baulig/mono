using Microsoft.Win32.SafeHandles;

using System.Diagnostics;
using System.Threading;

namespace System.Net.Sockets
{
	sealed class SocketAsyncContext
	{
		readonly SafeSocketHandle _socket;

		public SocketAsyncContext (SafeCloseSocket socket)
		{
			// CoreFX uses `SafeCloseSocket`, we are using `SafeSocketHandle`.
			_socket = (SafeSocketHandle)socket;
		}

		public void SetNonBlocking ()
		{
			Socket.Blocking_internal (_socket, false, out var error);
			if (error != 0)
				throw new SocketException (error);
		}

		public SocketError ConnectAsync (byte[] socketAddress, int socketAddressLen, Action<SocketError> callback)
		{
			SetNonBlocking ();

			var sa = new SocketAddress (socketAddress, socketAddressLen);

			Socket.Connect_internal (_socket, sa, out var error, false);
			Console.Error.WriteLine ($"DO OPERATION CONNECT: {error} {(SocketError)error}");

			if (error == 0)
				return SocketError.Success;

			if (error != (int) SocketError.InProgress && error != (int) SocketError.WouldBlock)
				return (SocketError)error;

			var sockares = new SocketAsyncResult ();

			IOSelector.Add (_socket.DangerousGetHandle (), new IOSelectorJob (IOOperation.Write, BeginConnectCallback, sockares));
			return SocketError.IOPending;
		}

		void BeginConnectCallback (IOAsyncResult ioares)
		{
			Console.Error.WriteLine ($"BEGIN CONNECT CALLBACK!");
		}
	}
}
