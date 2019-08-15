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

			var ares = new MySocketAsyncResult (callback);
			IOSelector.Add (_socket.DangerousGetHandle (), new IOSelectorJob (IOOperation.Write, BeginConnectCallback, ares));
			return SocketError.IOPending;
		}

		void BeginConnectCallback (IOAsyncResult ioares)
		{
			Console.Error.WriteLine ($"BEGIN CONNECT CALLBACK!");

			var myares = (MySocketAsyncResult)ioares;
			int error = (int)Socket.GetSocketOption (_socket, SocketOptionLevel.Socket, SocketOptionName.Error);
			Console.Error.WriteLine ($"BEGIN CONNECT CALLBACK #1: {(SocketError)error}");
			myares._callback((SocketError)error);
			return;

			var sockares = (SocketAsyncResult)ioares;

			if (sockares.EndPoint == null)
			{
				sockares.Complete(new SocketException((int)SocketError.AddressNotAvailable));
				return;
			}

			try
			{
				error = (int)sockares.socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error);

				if (error == 0)
				{
					sockares.socket.seed_endpoint = sockares.EndPoint;
					sockares.socket.is_connected = true;
					sockares.socket.is_bound = true;
					sockares.socket.connect_in_progress = false;
					sockares.error = 0;
					sockares.Complete();
					return;
				}

				if (sockares.Addresses == null)
				{
					sockares.socket.connect_in_progress = false;
					sockares.Complete(new SocketException(error));
					return;
				}

				if (sockares.CurrentAddress >= sockares.Addresses.Length)
				{
					sockares.Complete(new SocketException(error));
					return;
				}

				throw new NotImplementedException();
			}
			catch (Exception e)
			{
				sockares.socket.connect_in_progress = false;
				sockares.Complete(e);
			}
		}

		class MySocketAsyncResult : IOAsyncResult
		{
			public readonly Action<SocketError> _callback;

			public MySocketAsyncResult (Action<SocketError> callback)
			{
				_callback = callback;
			}

			internal override void CompleteDisposed ()
			{
				throw new NotImplementedException();
			}
		}
	}
}
