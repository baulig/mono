using Microsoft.Win32.SafeHandles;

using System.Diagnostics;
using System.Threading;

namespace System.Net.Sockets
{
	abstract class SafeCloseSocket : SafeHandleZeroOrMinusOneIsInvalid
	{
		protected SafeCloseSocket (bool ownsHandle) : base (ownsHandle)
		{
		}
	}
}
