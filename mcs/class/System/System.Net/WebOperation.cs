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

		public WebConnection Connection {
			get;
			private set;
		}

		public ServicePoint ServicePoint {
			get;
			private set;
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
			requestTask = new TaskCompletionSource<WebRequestStream> ();
			requestWrittenTask = new TaskCompletionSource<WebRequestStream> ();
			completeResponseReadTask = new TaskCompletionSource<bool> ();
			responseTask = new TaskCompletionSource<WebResponseStream> ();
			finishedTask = new TaskCompletionSource<bool> (); 
		}

		CancellationTokenSource cts;
		TaskCompletionSource<WebRequestStream> requestTask;
		TaskCompletionSource<WebRequestStream> requestWrittenTask;
		TaskCompletionSource<WebResponseStream> responseTask;
		TaskCompletionSource<bool> completeResponseReadTask;
		TaskCompletionSource<bool> finishedTask;
		WebRequestStream writeStream;
		WebResponseStream responseStream;
		ExceptionDispatchInfo disposedInfo;
		ExceptionDispatchInfo closedInfo;
		WebOperation priorityRequest;
		volatile bool finishedReading;
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
		}

		void SetCanceled ()
		{
			requestTask.TrySetCanceled ();
			requestWrittenTask.TrySetCanceled ();
			responseTask.TrySetCanceled ();
			completeResponseReadTask.TrySetCanceled ();
		}

		void SetError (Exception error)
		{
			requestTask.TrySetException (error);
			requestWrittenTask.TrySetException (error);
			responseTask.TrySetException (error);
			completeResponseReadTask.TrySetException (error);
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

		internal void RegisterRequest (ServicePoint servicePoint, WebConnection connection)
		{
			lock (this) {
				if (Interlocked.CompareExchange (ref requestSent, 1, 0) != 0)
					throw new InvalidOperationException ("Invalid nested call.");
				ServicePoint = servicePoint;
				Connection = connection;
			}

			cts.Token.Register (() => {
				Request.FinishedReading = true;
				SetDisposed (ref disposedInfo);
			});
		}

		public void SetPriorityRequest (WebOperation operation)
		{
			lock (this) {
				if (requestSent != 1 || ServicePoint == null || finishedReading)
					throw new InvalidOperationException ("Should never happen.");
				operation.RegisterRequest (ServicePoint, Connection);
				if (Interlocked.CompareExchange (ref priorityRequest, operation, null) != null)
					throw new InvalidOperationException ("Invalid nested request.");
			}
		}

		public Task<WebRequestStream> GetRequestStream ()
		{
			return requestTask.Task;
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

		internal Task<bool> WaitForCompletion ()
		{
			return finishedTask.Task;
		}

		internal async void Run ()
		{
			try {
				FinishReading ();

				ThrowIfClosedOrDisposed ();

				var requestStream = await Connection.InitConnection (this, cts.Token).ConfigureAwait (false);

				ThrowIfClosedOrDisposed ();

				writeStream = requestStream;

				await requestStream.Initialize (cts.Token).ConfigureAwait (false);

				ThrowIfClosedOrDisposed ();

				requestTask.TrySetResult (requestStream);

				var stream = new WebResponseStream (requestStream);

				await stream.InitReadAsync (cts.Token).ConfigureAwait (false);
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

		async void FinishReading ()
		{
			bool ok = false;
			Exception error = null;

			try {
				ok = await completeResponseReadTask.Task.ConfigureAwait (false);
			} catch (Exception e) {
				error = e;
			}

			WebResponseStream stream;
			WebOperation next;

			lock (this) {
				finishedReading = true;
				stream = Interlocked.Exchange (ref responseStream, null);
				next = Interlocked.Exchange (ref priorityRequest, null);
				Request.FinishedReading = true;
			}

			WebConnection.Debug ($"WO FINISH READING: Op={ID} {ok} {error != null} {stream != null} {next != null}");

			try {
				var keepAlive = await FinishReadingInner (ok, stream, next).ConfigureAwait (false);
				finishedTask.TrySetResult (keepAlive);
			} catch (Exception ex) {
				finishedTask.TrySetException (ex);
			}

			WebConnection.Debug ($"WO FINISH READING DONE: Op={ID}");
		}

		async Task<bool> FinishReadingInner (bool ok, WebResponseStream stream, WebOperation next)
		{
			bool keepAlive = false;

			if (ok && stream != null) {
				keepAlive = stream.KeepAlive;
			}

			WebConnection.Debug ($"WO FINISH READING #1: Op={ID} {stream != null} {keepAlive}");

			if (!Connection.Continue (ref keepAlive, next))
				return keepAlive;

			return await next.WaitForCompletion ().ConfigureAwait (false);
		}

		internal void CompleteRequestWritten (WebRequestStream stream, Exception error = null)
		{
			WebConnection.Debug ($"WO COMPLETE REQUEST WRITTEN: Op={ID} {error != null}");

			if (error != null)
				requestWrittenTask.TrySetException (error);
			else
				requestWrittenTask.TrySetResult (stream);
		}

		internal void CompleteResponseRead (WebResponseStream stream, bool ok, Exception error = null)
		{
			WebConnection.Debug ($"WO COMPLETE RESPONSE READ: Op={ID} {ok} {error?.GetType ()}");

			if (error != null)
				completeResponseReadTask.TrySetException (error);
			else
				completeResponseReadTask.TrySetResult (ok);
		}
	}
}
