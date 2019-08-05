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
        private void ConnectCompletionCallback(SocketError socketError)
        {
            CompletionCallback(0, SocketFlags.None, socketError);
        }

        internal unsafe SocketError DoOperationConnect(Socket socket, SafeCloseSocket handle)
        {
#if FIXME
            bool blk = socket.is_blocking;
            if (blk)
                socket.Blocking = false;
            Socket.Connect_internal(socket.m_Handle, _socketAddress, out var error, false);
            if (blk)
                socket.Blocking = true;
            Console.Error.WriteLine($"DO OPERATION CONNECT: {error} {(SocketError)error}");
#endif

            SocketError socketError = handle.AsyncContext.ConnectAsync(_socketAddress.Buffer, _socketAddress.Size, ConnectCompletionCallback);
            if (socketError != SocketError.IOPending)
            {
                FinishOperationSync(socketError, 0, SocketFlags.None);
            }
            return socketError;
        }

        private void FinishOperationSync(SocketError socketError, int bytesTransferred, SocketFlags flags)
        {
            Debug.Assert(socketError != SocketError.IOPending);

            if (socketError == SocketError.Success)
            {
                FinishOperationSyncSuccess(bytesTransferred, flags);
            }
            else
            {
                FinishOperationSyncFailure(socketError, bytesTransferred, flags);
            }
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

        private void CompletionCallback(int bytesTransferred, SocketFlags flags, SocketError socketError)
        {
            if (socketError == SocketError.Success)
            {
                FinishOperationAsyncSuccess(bytesTransferred, flags);
            }
            else
            {
                if (_currentSocket.CleanedUp)
                {
                    socketError = SocketError.OperationAborted;
                }

                FinishOperationAsyncFailure(socketError, bytesTransferred, flags);
            }
        }
    }
}
