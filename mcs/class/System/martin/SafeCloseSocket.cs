using Microsoft.Win32.SafeHandles;

using System.Diagnostics;
using System.Threading;

namespace System.Net.Sockets
{
    abstract class SafeCloseSocket : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SocketAsyncContext _asyncContext;

        internal bool LastConnectFailed { get; set; }

        protected SafeCloseSocket(bool ownsHandle) : base(ownsHandle)
        {
        }

        public SocketAsyncContext AsyncContext
        {
            get
            {
                if (Volatile.Read(ref _asyncContext) == null)
                {
                    Interlocked.CompareExchange(ref _asyncContext, new SocketAsyncContext(this), null);
                }

                return _asyncContext;
            }
        }

        public void RegisterConnectResult(SocketError error)
        {
            switch (error)
            {
                case SocketError.Success:
                case SocketError.WouldBlock:
                    break;
                default:
                    LastConnectFailed = true;
                    break;
            }
        }

        public bool IsDisconnected { get; private set; } = false;

        public void SetToDisconnected()
        {
            IsDisconnected = true;
        }
    }
}
