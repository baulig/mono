// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets
{
    public partial class SocketAsyncEventArgs : EventArgs, IDisposable
    {
        internal unsafe SocketError DoOperationConnect(Socket socket, SafeCloseSocket handle)
        {
            bool blk = socket.is_blocking;
            if (blk)
                socket.Blocking = false;
            Socket.Connect_internal(socket.m_Handle, _socketAddress, out var error, false);
            if (blk)
                socket.Blocking = true;
            Console.Error.WriteLine($"DO OPERATION CONNECT: {error} {(SocketError)error}");

#if MARTIN_FIXME
            SocketError socketError = handle.AsyncContext.ConnectAsync(_socketAddress.Buffer, _socketAddress.Size, ConnectCompletionCallback);
            if (socketError != SocketError.IOPending)
            {
                FinishOperationSync(socketError, 0, SocketFlags.None);
            }
            return socketError;
#else
            throw new NotImplementedException ();
#endif
        }

        private void InitializeInternals()
        {
            // No-op for *nix.
        }

        private void FreeInternals()
        {
            // No-op for *nix.
        }

        private void SetupSingleBuffer()
        {
            // No-op for *nix.
        }

        private void SetupMultipleBuffers()
        {
            // No-op for *nix.
        }

        private void CompleteCore() { }
    }
}
