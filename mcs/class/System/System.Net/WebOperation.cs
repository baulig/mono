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

		public bool IsNtlmChallenge {
			get;
		}

		static int nextID;
		public readonly int ID = ++nextID;

		public WebOperation (HttpWebRequest request, BufferOffsetSize writeBuffer, bool isNtlmChallenge, CancellationToken cancellationToken)
		{
			Request = request;
			WriteBuffer = writeBuffer;
			IsNtlmChallenge = isNtlmChallenge;
			cts = CancellationTokenSource.CreateLinkedTokenSource (cancellationToken);
			requestTask = new TaskCompletionSource<(WebConnectionData data, WebRequestStream stream)> ();
			requestWrittenTask = new TaskCompletionSource<WebRequestStream> ();
			responseTask = new TaskCompletionSource<WebResponseStream> ();
		}

		CancellationTokenSource cts;
		TaskCompletionSource<(WebConnectionData data, WebRequestStream stream)> requestTask;
		TaskCompletionSource<WebRequestStream> requestWrittenTask;
		TaskCompletionSource<WebResponseStream> responseTask;
		WebConnectionData connectionData;
		WebRequestStream writeStream;
		WebResponseStream responseStream;
		ExceptionDispatchInfo disposedInfo;
		ExceptionDispatchInfo closedInfo;
		int requestSent;

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
			SetCanceled ();
			Close (); 
		}

		public void Close ()
		{
			var (exception, closed) = SetDisposed (ref closedInfo);
			if (!closed)
				return;

			var stream = Interlocked.Exchange (ref writeStream, null);
			if (stream != null) {
				try {
					stream.Close ();
				} catch { }
			}

			var data = Interlocked.Exchange (ref connectionData, null);
			if (data != null) {
				try {
					data.Close ();
				} catch { }
			}
		}

		void SetCanceled ()
		{
			requestTask.TrySetCanceled ();
			requestWrittenTask.TrySetCanceled ();
			responseTask.TrySetCanceled ();
		}

		void SetError (Exception error)
		{
			requestTask.TrySetException (error);
			requestWrittenTask.TrySetException (error);
			responseTask.TrySetException (error);
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
			if (Interlocked.CompareExchange (ref requestSent, 1, 0) != 0)
				throw new InvalidOperationException ("Invalid nested call.");
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

		public Task<WebResponseStream> GetResponseStream ()
		{
			return responseTask.Task;
		}

		internal async void Run (WebConnection connection)
		{
			try {
				ThrowIfClosedOrDisposed ();
				var (data, requestStream) = await connection.InitConnection (this, cts.Token).ConfigureAwait (false);
				if (data == null || Aborted) {
					SetCanceled ();
					return;
				}

				writeStream = requestStream;
				connectionData = data;

				ThrowIfClosedOrDisposed ();

				await requestStream.Initialize (cts.Token).ConfigureAwait (false);

				ThrowIfClosedOrDisposed ();

				requestTask.TrySetResult ((data, requestStream));

				var stream = await connection.InitReadAsync (this, data, cts.Token).ConfigureAwait (false);
				responseStream = stream;

				responseTask.TrySetResult (stream);
			} catch (OperationCanceledException) {
				SetCanceled ();
			} catch (Exception e) {
				SetError (e);
			} finally {
				// cts.Dispose ();
				// cts = null;
			}
		}

		internal void CompleteRequestWritten (WebRequestStream stream, Exception error = null)
		{
			WebConnection.Debug ($"WO COMPLETE REQUEST WRITTEN: {ID} {error != null}");

			if (error != null)
				requestWrittenTask.TrySetException (error);
			else
				requestWrittenTask.TrySetResult (stream);
		}
	}
}
