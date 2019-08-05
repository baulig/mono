using Microsoft.Win32.SafeHandles;

using System.Diagnostics;
using System.Threading;

namespace System.Net.Sockets
{
	abstract class SafeCloseSocket : SafeHandleZeroOrMinusOneIsInvalid
	{
		SocketAsyncContext _asyncContext;

		protected SafeCloseSocket (bool ownsHandle) : base (ownsHandle)
		{
		}

		public SocketAsyncContext AsyncContext {
			get {
				if (Volatile.Read (ref _asyncContext) == null)
					Interlocked.CompareExchange (ref _asyncContext, new SocketAsyncContext (this), null);

				return _asyncContext;
			}
		}

	}
}
