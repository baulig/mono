// System.Net.Sockets.SocketAsyncEventArgs.cs
//
// Authors:
//	Marek Habersack (mhabersack@novell.com)
//	Gonzalo Paniagua Javier (gonzalo@xamarin.com)
//
// Copyright (c) 2008,2010 Novell, Inc. (http://www.novell.com)
// Copyright (c) 2011 Xamarin, Inc. (http://xamarin.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security;
using System.Threading;

namespace System.Net.Sockets
{
	public partial class SocketAsyncEventArgs : EventArgs, IDisposable
	{
		bool disposed;

		internal volatile int in_progress;
		internal EndPoint remote_ep;
		internal Socket current_socket;

		internal SocketAsyncResult socket_async_result = new SocketAsyncResult ();

		internal IList<ArraySegment<byte>> m_BufferList;

		internal bool PolicyRestricted {
			get;
			private set;
		}

		internal SocketAsyncEventArgs (bool policy)
			: this ()
		{
			PolicyRestricted = policy;
		}

		public SocketAsyncEventArgs ()
		{
			SendPacketsSendSize = -1;
		}

		internal void SetLastOperation (SocketAsyncOperation op)
		{
			if (disposed)
				throw new ObjectDisposedException ("System.Net.Sockets.SocketAsyncEventArgs");
			if (Interlocked.Exchange (ref in_progress, 1) != 0)
				throw new InvalidOperationException ("Operation already in progress");

			LastOperation = op;
		}

		internal void StartOperationCommon (Socket socket)
		{
			current_socket = socket;
		}

		internal void StartOperationWrapperConnect (MultipleConnectAsync args)
		{
			SetLastOperation (SocketAsyncOperation.Connect);

			//m_MultipleConnect = args;
		}

		internal void FinishConnectByNameSyncFailure (Exception exception, int bytesTransferred, SocketFlags flags)
		{
			SetResults (exception, bytesTransferred, flags);

			if (current_socket != null)
				current_socket.is_connected = false;
			
			Complete ();
		}

		internal void FinishOperationAsyncFailure (Exception exception, int bytesTransferred, SocketFlags flags)
		{
			SetResults (exception, bytesTransferred, flags);

			if (current_socket != null)
				current_socket.is_connected = false;
			
			Complete ();
		}

		internal void FinishWrapperConnectSuccess (Socket connectSocket, int bytesTransferred, SocketFlags flags)
		{
			SetResults(SocketError.Success, bytesTransferred, flags);
			current_socket = connectSocket;

			Complete ();
		}
	}
}
