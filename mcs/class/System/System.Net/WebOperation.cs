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

		public BufferOffsetSize WriteBuffer {
			get;
		}

		static int nextID;
		public readonly int ID = ++nextID;

		public WebOperation (HttpWebRequest request, BufferOffsetSize writeBuffer, CancellationToken cancellationToken)
		{
			Request = request;
			WriteBuffer = writeBuffer;
			cts = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			requestTask = new TaskCompletionSource<(WebConnectionData data, WebRequestStream stream)> ();
			responseDataTask = new TaskCompletionSource<WebConnectionData> ();
			requestWrittenTask = new TaskCompletionSource<WebRequestStream> (); 
		}

		CancellationTokenSource cts;
		TaskCompletionSource<(WebConnectionData data, WebRequestStream stream)> requestTask;
		TaskCompletionSource<WebConnectionData> responseDataTask;
		TaskCompletionSource<WebRequestStream> requestWrittenTask;
		WebRequestStream writeStream;
		ExceptionDispatchInfo disposedInfo;
		ExceptionDispatchInfo closedInfo;

		public bool Aborted {
			get {
				if (disposedInfo != null || Request.Aborted)
					return true;
				if (cts != null && cts.IsCancellationRequested)
					return true;
				return false;
			}
		}

		public bool Closed {
			get {
				return Aborted || closedInfo != null;
			}
		}

		public void Abort ()
		{
			var (exception, disposed) = SetDisposed (ref disposedInfo);
			if (!disposed)
				return;
			cts?.Cancel ();
			requestTask.TrySetCanceled ();
			responseDataTask.TrySetCanceled ();
			requestWrittenTask.TrySetCanceled ();
			Close (); 
		}

		public void Close ()
		{
			var (exception, closed) = SetDisposed (ref closedInfo);
			if (!closed)
				return;

			if (writeStream != null) {
				try {
					writeStream.Close ();
				} catch { }
				writeStream = null;
			}
		}

		public void SetError (Exception error)
		{
			responseDataTask.TrySetException (error);
		}

		(ExceptionDispatchInfo, bool) SetDisposed (ref ExceptionDispatchInfo field)
		{
			var wexc = new WebException (SR.GetString (SR.net_webstatus_RequestCanceled), WebExceptionStatus.RequestCanceled);
			var exception = ExceptionDispatchInfo.Capture (wexc);
			var old = Interlocked.CompareExchange (ref field, exception, null);
			return (old ?? exception, old == null);
		}

		internal void ThrowIfDisposed ()
		{
			ThrowIfDisposed (CancellationToken.None);
		}

		internal void ThrowIfDisposed (CancellationToken cancellationToken)
		{
			if (Aborted || cancellationToken.IsCancellationRequested)
				ThrowDisposed (ref disposedInfo);
		}

		internal void ThrowIfClosedOrDisposed ()
		{
			ThrowIfClosedOrDisposed (CancellationToken.None);
		}

		internal void ThrowIfClosedOrDisposed (CancellationToken cancellationToken)
		{
			if (Closed || cancellationToken.IsCancellationRequested)
				ThrowDisposed (ref closedInfo);
		}

		void ThrowDisposed (ref ExceptionDispatchInfo field)
		{
			var (exception, disposed) = SetDisposed (ref field);
			if (disposed)
				cts?.Cancel ();
			exception.Throw ();
		}

		public void SendRequest (WebConnection connection)
		{
			cts.Token.Register (() => {
				SetDisposed (ref disposedInfo);
				connection.Abort (this);
			});
			connection.SendRequest (this);
		}

		public async Task<WebRequestStream> GetRequestStream ()
		{
			var result = await requestTask.Task.ConfigureAwait (false);
			return result.stream;
		}

		public Task WaitUntilRequestWritten ()
		{
			return requestWrittenTask.Task;
		}

		public WebRequestStream WriteStream {
			get {
				ThrowIfDisposed ();
				return writeStream;
			}
		}

		public TaskCompletionSource<WebConnectionData> ResponseDataTask => responseDataTask;

		internal async void Run (Func<CancellationToken, Task<(WebConnectionData, WebRequestStream, Exception)>> func)
		{
			try {
				if (Aborted) {
					requestTask.TrySetCanceled ();
					return;
				}
				var (data, stream, error) = await func (cts.Token).ConfigureAwait (false);
				if (data == null || Aborted) {
					requestTask.TrySetCanceled ();
					responseDataTask.TrySetCanceled ();
					return;
				}
				if (error != null) {
					requestTask.TrySetException (error);
					responseDataTask.TrySetException (error);
					return;
				}

				writeStream = stream;
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

		internal void CompleteRequestWritten (WebRequestStream stream, Exception error = null)
		{
			if (error != null)
				requestWrittenTask.TrySetException (error);
			else
				requestWrittenTask.TrySetResult (stream);
		}
	}
}
