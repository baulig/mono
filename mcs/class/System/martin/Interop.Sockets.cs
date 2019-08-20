using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Sys
    {
        internal static unsafe Error Accept (SafeHandle safeHandle, byte *socketAddress, int *socketAddressLen, IntPtr *acceptedFd)
        {
            var socketHandle = (SafeSocketHandle)safeHandle;
            try {
                socketHandle.RegisterForBlockingSyscall ();
                var errno = Socket_Accept_internal (socketHandle.DangerousGetHandle (), socketAddress, socketAddressLen, acceptedFd);
                return Interop.Sys.ConvertErrorPlatformToPal(errno);
            } finally {
                socketHandle.UnRegisterForBlockingSyscall ();
            }
        }

        [MethodImplAttribute (MethodImplOptions.InternalCall)]
        private static extern unsafe int Socket_Accept_internal(IntPtr socket, byte* socketAddress, int* socketAddressLen, IntPtr* acceptedFd);
    }
}
