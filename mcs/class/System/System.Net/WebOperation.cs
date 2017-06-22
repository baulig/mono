//
// WebOperation.cs
//
// Author:
//       Martin Baulig <mabaul@microsoft.com>
//
// Copyright (c) 2017 Xamarin Inc. (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
#define MARTIN_DEBUG
using System.IO;
using System.Collections;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Diagnostics;

namespace System.Net
{
	class WebOperation
	{
		public HttpWebRequest Request {
			get;
		}

		static int nextID;
		public readonly int ID = ++nextID;

		public WebOperation (HttpWebRequest request, CancellationToken cancellationToken)
		{
			Request = request;
			cts = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
		}

		CancellationTokenSource cts;
		TaskCompletionSource<(WebConnectionData,WebConnectionStream)> requestTask;
		ExceptionDispatchInfo disposed;

		public bool Aborted {
			get {
				if (disposed != null || Request.Aborted)
					return true;
				if (cts != null && cts.IsCancellationRequested)
					return true;
				return false;
			}
		}

		public void Abort ()
		{
			var (exception, disposed) = SetDisposed ();
			if (disposed)
				cts?.Cancel ();
		}

		(ExceptionDispatchInfo, bool) SetDisposed ()
		{
			var exception = ExceptionDispatchInfo.Capture (new ObjectDisposedException (GetType ().ToString ()));
			var old = Interlocked.CompareExchange (ref disposed, exception, null);
			return (old ?? exception, old == null);
		}

		internal void ThrowIfDisposed ()
		{
			ThrowIfDisposed (CancellationToken.None);
		}

		internal void ThrowIfDisposed (CancellationToken cancellationToken)
		{
			if (Aborted || cancellationToken.IsCancellationRequested) {
				var (exception, disposed) = SetDisposed ();
				if (disposed)
					cts?.Cancel ();
				exception.Throw ();
			}
		}

		public void SendRequest (WebConnection connection)
		{
			var task = new TaskCompletionSource<(WebConnectionData, WebConnectionStream)> ();
			if (Interlocked.CompareExchange (ref requestTask, task, null) != null)
				throw new InvalidOperationException ("Invalid nested call!");

			cts.Token.Register (() => {
				SetDisposed ();
				connection.Abort (this);
			});
			connection.SendRequest (this);
		}

		public async void Run (Func<CancellationToken, Task<(WebConnectionData, WebConnectionStream, Exception)>> func)
		{
			try {
				if (Aborted) {
					requestTask.TrySetCanceled ();
					return;
				}
				var (data, stream, error) = await func (cts.Token).ConfigureAwait (false);
				if (error != null)
					requestTask.TrySetException (error);
				else if (data == null || Aborted)
					requestTask.TrySetCanceled ();
				else
					requestTask.TrySetResult ((data, stream));
			} catch (OperationCanceledException) {
				requestTask.TrySetCanceled ();
			} catch (Exception e) {
				requestTask.TrySetException (e);
			} finally {
				cts.Dispose ();
				cts = null;
			}
		}
	}
}
