using Microsoft.Win32.SafeHandles;

using System.Diagnostics;
using System.Threading;

namespace System.Net.Sockets
{
	sealed class SocketAsyncContext
	{
		readonly SafeCloseSocket _socket;

		public SocketAsyncContext (SafeCloseSocket socket)
		{
			_socket = socket;
		}

		public SocketError ConnectAsync (byte[] socketAddress, int socketAddressLen, Action<SocketError> callback)
		{
			throw new NotImplementedException ();
		}
	}
}
