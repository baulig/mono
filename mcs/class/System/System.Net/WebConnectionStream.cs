//
// System.Net.WebConnectionStream
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
// (C) 2004 Novell, Inc (http://www.novell.com)
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
#define MARTIN_DEBUG
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Net.Sockets;

namespace System.Net
{
	// https://blogs.msdn.microsoft.com/pfxteam/2012/02/11/building-async-coordination-primitives-part-1-asyncmanualresetevent/
	class AsyncManualResetEvent
	{
		volatile TaskCompletionSource<bool> m_tcs = new TaskCompletionSource<bool> ();

		public Task WaitAsync () { return m_tcs.Task; }

		public bool WaitOne (int millisecondTimeout)
		{
			WebConnection.Debug ($"AMRE WAIT ONE: {millisecondTimeout}");
			return m_tcs.Task.Wait (millisecondTimeout);
		}

		public async Task<bool> WaitAsync (int millisecondTimeout)
		{
			var timeoutTask = Task.Delay (millisecondTimeout);
			var ret = await Task.WhenAny (m_tcs.Task, timeoutTask).ConfigureAwait (false);
			return ret != timeoutTask;
		}

		public void Set ()
		{
			var tcs = m_tcs;
			Task.Factory.StartNew (s => ((TaskCompletionSource<bool>)s).TrySetResult (true),
			    tcs, CancellationToken.None, TaskCreationOptions.PreferFairness, TaskScheduler.Default);
			tcs.Task.Wait ();
		}

		public void Reset ()
		{
			while (true) {
				var tcs = m_tcs;
				if (!tcs.Task.IsCompleted ||
				    Interlocked.CompareExchange (ref m_tcs, new TaskCompletionSource<bool> (), tcs) == tcs)
					return;
			}
		}

		public AsyncManualResetEvent (bool state)
		{
			if (state)
				Set ();
		}
	}

	class WebConnectionStream : Stream
	{
		static byte[] crlf = new byte[] { 13, 10 };
		bool isRead;
		WebConnection cnc;
		HttpWebRequest request;
		byte[] readBuffer;
		int readBufferOffset;
		int readBufferSize;
		int stream_length; // -1 when CL not present
		long contentLength;
		long totalRead;
		internal long totalWritten;
		bool nextReadCalled;
		int pendingReads;
		int pendingWrites;
		AsyncManualResetEvent pending;
		bool allowBuffering;
		bool sendChunked;
		MemoryStream writeBuffer;
		bool requestWritten;
		byte[] headers;
		bool disposed;
		bool headersSent;
		object locker = new object ();
		TaskCompletionSource<int> readTcs;
		TaskCompletionSource<int> pendingWrite;
		int nestedRead;
		int nestedWrite;
		bool initRead;
		bool read_eof;
		bool complete_request_written;
		int read_timeout;
		int write_timeout;
		AsyncCallback cb_wrapper; // Calls to ReadCallbackWrapper or WriteCallbacWrapper
		internal bool IgnoreIOErrors;

		public WebConnectionStream (WebConnection cnc, WebConnectionData data)
		{
			if (data == null)
				throw new InvalidOperationException ("data was not initialized");
			if (data.Headers == null)
				throw new InvalidOperationException ("data.Headers was not initialized");
			if (data.request == null)
				throw new InvalidOperationException ("data.request was not initialized");
			isRead = true;
			cb_wrapper = new AsyncCallback (ReadCallbackWrapper);
			pending = new AsyncManualResetEvent (true);
			this.request = data.request;
			read_timeout = request.ReadWriteTimeout;
			write_timeout = read_timeout;
			this.cnc = cnc;
			string contentType = data.Headers["Transfer-Encoding"];
			bool chunkedRead = (contentType != null && contentType.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			string clength = data.Headers["Content-Length"];
			if (!chunkedRead && clength != null && clength != "") {
				try {
					contentLength = Int32.Parse (clength);
					if (contentLength == 0 && !IsNtlmAuth ()) {
						ReadAll ();
					}
				} catch {
					contentLength = Int64.MaxValue;
				}
			} else {
				contentLength = Int64.MaxValue;
			}

			// Negative numbers?
			if (!Int32.TryParse (clength, out stream_length))
				stream_length = -1;
		}

		public WebConnectionStream (WebConnection cnc, HttpWebRequest request)
		{
			read_timeout = request.ReadWriteTimeout;
			write_timeout = read_timeout;
			isRead = false;
			cb_wrapper = new AsyncCallback (WriteCallbackWrapper);
			this.cnc = cnc;
			this.request = request;
			allowBuffering = request.InternalAllowBuffering;
			sendChunked = request.SendChunked;
			if (sendChunked)
				pending = new AsyncManualResetEvent (true);
			else if (allowBuffering)
				writeBuffer = new MemoryStream ();
		}

		bool CheckAuthHeader (string headerName)
		{
			var authHeader = cnc.Data.Headers[headerName];
			return (authHeader != null && authHeader.IndexOf ("NTLM", StringComparison.Ordinal) != -1);
		}

		bool IsNtlmAuth ()
		{
			bool isProxy = (request.Proxy != null && !request.Proxy.IsBypassed (request.Address));
			if (isProxy && CheckAuthHeader ("Proxy-Authenticate"))
				return true;
			return CheckAuthHeader ("WWW-Authenticate");
		}

		internal void CheckResponseInBuffer ()
		{
			if (contentLength > 0 && (readBufferSize - readBufferOffset) >= contentLength) {
				if (!IsNtlmAuth ())
					ReadAll ();
			}
		}

		internal HttpWebRequest Request {
			get { return request; }
		}

		internal WebConnection Connection {
			get { return cnc; }
		}
		public override bool CanTimeout {
			get { return true; }
		}

		public override int ReadTimeout {
			get {
				return read_timeout;
			}

			set {
				if (value < -1)
					throw new ArgumentOutOfRangeException ("value");
				read_timeout = value;
			}
		}

		public override int WriteTimeout {
			get {
				return write_timeout;
			}

			set {
				if (value < -1)
					throw new ArgumentOutOfRangeException ("value");
				write_timeout = value;
			}
		}

		internal bool CompleteRequestWritten {
			get { return complete_request_written; }
		}

		internal bool SendChunked {
			set { sendChunked = value; }
		}

		internal byte[] ReadBuffer {
			set { readBuffer = value; }
		}

		internal int ReadBufferOffset {
			set { readBufferOffset = value; }
		}

		internal int ReadBufferSize {
			set { readBufferSize = value; }
		}

		internal byte[] WriteBuffer {
			get { return writeBuffer.GetBuffer (); }
		}

		internal int WriteBufferLength {
			get { return writeBuffer != null ? (int)writeBuffer.Length : (-1); }
		}

		internal void ForceCompletion ()
		{
			if (!nextReadCalled) {
				if (contentLength == Int64.MaxValue)
					contentLength = 0;
				nextReadCalled = true;
				cnc.NextRead ();
			}
		}

		internal void CheckComplete ()
		{
			bool nrc = nextReadCalled;
			if (!nrc && readBufferSize - readBufferOffset == contentLength) {
				nextReadCalled = true;
				cnc.NextRead ();
			}
		}

		internal async Task ReadAllAsync (CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS READ ALL ASYNC: {cnc.ID}");
			if (!isRead || read_eof || totalRead >= contentLength || nextReadCalled) {
				if (isRead && !nextReadCalled) {
					nextReadCalled = true;
					cnc.NextRead ();
				}
				return;
			}

			var timeoutTask = Task.Delay (ReadTimeout);
			var myReadTcs = new TaskCompletionSource<int> ();
			while (true) {
				/*
				 * 'readTcs' is set by ReadAsync().
				 */
				cancellationToken.ThrowIfCancellationRequested ();
				var oldReadTcs = Interlocked.CompareExchange (ref readTcs, myReadTcs, null);
				if (oldReadTcs == null)
					break;

				// ReadAsync() is in progress.
				var anyTask = await Task.WhenAny (oldReadTcs.Task, timeoutTask).ConfigureAwait (false);
				if (anyTask == timeoutTask)
					throw new WebException ("The operation has timed out.", WebExceptionStatus.Timeout);
			}

			WebConnection.Debug ($"WCS READ ALL ASYNC #1: {cnc.ID}");

			cancellationToken.ThrowIfCancellationRequested ();

			try {
				if (totalRead >= contentLength)
					return;

				byte[] b = null;
				int diff = readBufferSize - readBufferOffset;
				int new_size;

				if (contentLength == Int64.MaxValue) {
					MemoryStream ms = new MemoryStream ();
					byte[] buffer = null;
					if (readBuffer != null && diff > 0) {
						ms.Write (readBuffer, readBufferOffset, diff);
						if (readBufferSize >= 8192)
							buffer = readBuffer;
					}

					if (buffer == null)
						buffer = new byte[8192];

					int read;
					while ((read = await cnc.ReadAsync (request, buffer, 0, buffer.Length, cancellationToken)) != 0)
						ms.Write (buffer, 0, read);

					b = ms.GetBuffer ();
					new_size = (int)ms.Length;
					contentLength = new_size;
				} else {
					new_size = (int)(contentLength - totalRead);
					b = new byte[new_size];
					if (readBuffer != null && diff > 0) {
						if (diff > new_size)
							diff = new_size;

						Buffer.BlockCopy (readBuffer, readBufferOffset, b, 0, diff);
					}

					int remaining = new_size - diff;
					int r = -1;
					while (remaining > 0 && r != 0) {
						r = await cnc.ReadAsync (request, b, diff, remaining, cancellationToken);
						remaining -= r;
						diff += r;
					}
				}

				readBuffer = b;
				readBufferOffset = 0;
				readBufferSize = new_size;
				totalRead = 0;
				nextReadCalled = true;
				myReadTcs.TrySetResult (new_size);
			} catch (Exception ex) {
				WebConnection.Debug ($"WCS READ ALL ASYNC EX: {cnc.ID} {ex.Message}");
				myReadTcs.TrySetException (ex);
				throw;
			} finally {
				WebConnection.Debug ($"WCS READ ALL #2: {cnc.ID}");
				readTcs = null;
			}

			cnc.NextRead ();
		}

		[Obsolete ("Use ReadAllAsync()")]
		internal void ReadAll ()
		{
			ReadAllAsync (CancellationToken.None).Wait ();
		}

		void WriteCallbackWrapper (IAsyncResult r)
		{
			WebAsyncResult result = r as WebAsyncResult;
			if (result != null && result.AsyncWriteAll)
				return;

			if (r.AsyncState != null) {
				result = (WebAsyncResult)r.AsyncState;
				result.InnerAsyncResult = r;
				result.DoCallback ();
			} else {
				try {
					EndWrite (r);
				} catch {
				}
			}
		}

		void ReadCallbackWrapper (IAsyncResult r)
		{
			WebAsyncResult result;
			if (r.AsyncState != null) {
				result = (WebAsyncResult)r.AsyncState;
				result.InnerAsyncResult = r;
				result.DoCallback ();
			} else {
				try {
					EndRead (r);
				} catch {
				}
			}
		}

		public override int Read (byte[] buffer, int offset, int size)
		{
			WebConnection.Debug ($"WCS READ: {cnc.ID}");
			return ReadAsync (buffer, offset, size, CancellationToken.None).Result;
		}

		public override async Task<int> ReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS READ ASYNC: {cnc.ID}");

			cancellationToken.ThrowIfCancellationRequested ();

			if (!isRead)
				throw new NotSupportedException ("this stream does not allow reading");
			if (buffer == null)
				throw new ArgumentNullException ("buffer");

			int length = buffer.Length;
			if (offset < 0 || length < offset)
				throw new ArgumentOutOfRangeException ("offset");
			if (size < 0 || (length - offset) < size)
				throw new ArgumentOutOfRangeException ("size");

			if (Interlocked.CompareExchange (ref nestedRead, 1, 0) != 0)
				throw new InvalidOperationException ("Invalid nested call.");

			var myReadTcs = new TaskCompletionSource<int> ();
			while (!cancellationToken.IsCancellationRequested) {
				/*
				 * 'readTcs' is set by ReadAllAsync().
				 */
				var oldReadTcs = Interlocked.CompareExchange (ref readTcs, myReadTcs, null);
				WebConnection.Debug ($"WCS READ ASYNC #1: {cnc.ID} {oldReadTcs != null}");
				if (oldReadTcs == null)
					break;
				await oldReadTcs.Task.ConfigureAwait (false);
			}

			WebConnection.Debug ($"WCS READ ASYNC #2: {cnc.ID} {totalRead} {contentLength}");

			if (totalRead >= contentLength || cancellationToken.IsCancellationRequested)
				return await FinishReadAsync (0, -1, null, cancellationToken).ConfigureAwait (false);

			int oldBytes = 0;
			int remaining = readBufferSize - readBufferOffset;
			if (remaining > 0) {
				int copy = (remaining > size) ? size : remaining;
				Buffer.BlockCopy (readBuffer, readBufferOffset, buffer, offset, copy);
				readBufferOffset += copy;
				offset += copy;
				size -= copy;
				totalRead += copy;
				if (size == 0 || totalRead >= contentLength)
					return await FinishReadAsync (0, copy, null, cancellationToken).ConfigureAwait (false);
				oldBytes = copy;
			}

			if (contentLength != Int64.MaxValue && contentLength - totalRead < size)
				size = (int)(contentLength - totalRead);

			WebConnection.Debug ($"WCS READ ASYNC #1: {cnc.ID} {oldBytes} {size} {read_eof}");

			if (read_eof)
				return await FinishReadAsync (oldBytes, 0, null, cancellationToken).ConfigureAwait (false);
			if (cancellationToken.IsCancellationRequested)
				return await FinishReadAsync (0, -1, null, cancellationToken).ConfigureAwait (false);

			try {
				var ret = await cnc.ReadAsync (request, buffer, offset, size, cancellationToken).ConfigureAwait (false);
				return await FinishReadAsync (oldBytes, ret, null, cancellationToken).ConfigureAwait (false);
			} catch (Exception ex) {
				var error = ExceptionDispatchInfo.Capture (ex);
				return await FinishReadAsync (-1, -1, error, cancellationToken).ConfigureAwait (false);
			}
		}

		async Task<int> FinishReadAsync (int oldBytes, int nbytes, ExceptionDispatchInfo error, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS FINISH READ ASYNC: {cnc.ID} {oldBytes} {nbytes} {error != null}");

			if (cancellationToken.IsCancellationRequested && error == null)
				error = ExceptionDispatchInfo.Capture (new OperationCanceledException ("WebConnectionStream.ReadAsync()"));

			if (error != null) {
				lock (locker) {
					readTcs.TrySetException (error.SourceException);
					readTcs = null;
					nestedRead = 0;
					pendingReads--;
					if (pendingReads == 0)
						pending.Set ();
				}

				nextReadCalled = true;
				cnc.Close (true);
				error.Throw ();
				return -1;
			}

			if (nbytes < 0) {
				nbytes = 0;
				read_eof = true;
			}

			totalRead += nbytes;
			if (nbytes == 0)
				contentLength = totalRead;

			lock (locker) {
				readTcs.TrySetResult (oldBytes + nbytes);
				readTcs = null;
				nestedRead = 0;
				pendingReads--;
				if (pendingReads == 0)
					pending.Set ();
			}

			if (totalRead >= contentLength && !nextReadCalled) {
				WebConnection.Debug ($"WCS READ ASYNC - READ ALL: {cnc.ID} {oldBytes} {nbytes} {error != null}");
				await ReadAllAsync (cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WCS READ ASYNC - READ ALL DONE: {cnc.ID} {oldBytes} {nbytes} {error != null}");
			}

			return oldBytes + nbytes;
		}

		public override IAsyncResult BeginRead (byte[] buffer, int offset, int size,
							AsyncCallback cb, object state)
		{
			WebConnection.Debug ($"WCS BEGIN READ: {cnc.ID}");

			var task = ReadAsync (buffer, offset, size, CancellationToken.None);
			return TaskToApm.Begin (task, cb, state);
		}

		public override int EndRead (IAsyncResult r)
		{
			WebConnection.Debug ($"WCS END READ: {cnc.ID}");
			return TaskToApm.End<int> (r);
		}

		public override async Task WriteAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS WRITE ASYNC: {cnc.ID}");

			cancellationToken.ThrowIfCancellationRequested ();

			if (request.Aborted)
				throw new WebException (SR.GetString (SR.net_webstatus_RequestCanceled));
			if (isRead)
				throw new NotSupportedException (SR.GetString (SR.net_readonlystream));

			if (buffer == null)
				throw new ArgumentNullException ("buffer");

			int length = buffer.Length;
			if (offset < 0 || length < offset)
				throw new ArgumentOutOfRangeException ("offset");
			if (size < 0 || (length - offset) < size)
				throw new ArgumentOutOfRangeException ("size");

			var myWriteTcs = new TaskCompletionSource<int> ();
			if (Interlocked.CompareExchange (ref pendingWrite, myWriteTcs, null) != null)
				throw new InvalidOperationException (SR.GetString (SR.net_repcall));

			try {
				await ProcessWrite (buffer, offset, size, cancellationToken).ConfigureAwait (false);

				if (!initRead) {
					initRead = true;
					cnc.InitRead ();
				}

				if (allowBuffering && !sendChunked && request.ContentLength > 0 && totalWritten == request.ContentLength)
					complete_request_written = true;

				pendingWrite = null;
				myWriteTcs.TrySetResult (0);
			} catch (Exception ex) {
				KillBuffer ();
				nextReadCalled = true;
				cnc.Close (true);

				if (ex is SocketException)
					ex = new IOException ("Error writing request", ex);

				pendingWrite = null;
				myWriteTcs.TrySetException (ex);
				throw;
			}

		}

		async Task ProcessWrite (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			bool asyncWriteAll = false;

			if (sendChunked) {
				requestWritten = true;

				string cSize = String.Format ("{0:X}\r\n", size);
				byte[] head = Encoding.ASCII.GetBytes (cSize);
				int chunkSize = 2 + size + head.Length;
				byte[] newBuffer = new byte[chunkSize];
				Buffer.BlockCopy (head, 0, newBuffer, 0, head.Length);
				Buffer.BlockCopy (buffer, offset, newBuffer, head.Length, size);
				Buffer.BlockCopy (crlf, 0, newBuffer, head.Length + size, crlf.Length);

				if (allowBuffering) {
					if (writeBuffer == null)
						writeBuffer = new MemoryStream ();
					writeBuffer.Write (buffer, offset, size);
					totalWritten += size;
				}

				buffer = newBuffer;
				offset = 0;
				size = chunkSize;
			} else {
				CheckWriteOverflow (request.ContentLength, totalWritten, size);

				if (allowBuffering) {
					if (writeBuffer == null)
						writeBuffer = new MemoryStream ();
					writeBuffer.Write (buffer, offset, size);
					totalWritten += size;

					if (request.ContentLength <= 0 || totalWritten < request.ContentLength)
						return;

					asyncWriteAll = true;
					requestWritten = true;
					buffer = writeBuffer.GetBuffer ();
					offset = 0;
					size = (int)totalWritten;
				}
			}

			try {
				await cnc.WriteAsync (request, buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (Exception ex) {
				if (!IgnoreIOErrors)
					throw;
			}
			totalWritten += size;
		}

		void WriteAsyncCB (IAsyncResult r)
		{
			WebAsyncResult result = (WebAsyncResult)r.AsyncState;
			result.InnerAsyncResult = null;

			try {
				cnc.EndWrite (r, true);
				result.SetCompleted (false, 0);
				if (!initRead) {
					initRead = true;
					cnc.InitRead ();
				}
			} catch (Exception e) {
				KillBuffer ();
				nextReadCalled = true;
				cnc.Close (true);
				if (e is System.Net.Sockets.SocketException)
					e = new IOException ("Error writing request", e);
				result.SetCompleted (false, e);
			}

			if (allowBuffering && !sendChunked && request.ContentLength > 0 && totalWritten == request.ContentLength)
				complete_request_written = true;

			result.DoCallback ();
		}

		public override IAsyncResult BeginWrite (byte[] buffer, int offset, int size,
							 AsyncCallback cb, object state)
		{
			if (request.Aborted)
				throw new WebException ("The request was canceled.", WebExceptionStatus.RequestCanceled);

			var task = WriteAsync (buffer, offset, size, CancellationToken.None);
			return TaskToApm.Begin (task, cb, state);

			if (isRead)
				throw new NotSupportedException ("this stream does not allow writing");

			if (buffer == null)
				throw new ArgumentNullException ("buffer");

			int length = buffer.Length;
			if (offset < 0 || length < offset)
				throw new ArgumentOutOfRangeException ("offset");
			if (size < 0 || (length - offset) < size)
				throw new ArgumentOutOfRangeException ("size");

			if (sendChunked) {
				lock (locker) {
					pendingWrites++;
					pending.Reset ();
				}
			}

			WebAsyncResult result = new WebAsyncResult (cb, state);
			AsyncCallback callback = new AsyncCallback (WriteAsyncCB);

			if (sendChunked) {
				requestWritten = true;

				string cSize = String.Format ("{0:X}\r\n", size);
				byte[] head = Encoding.ASCII.GetBytes (cSize);
				int chunkSize = 2 + size + head.Length;
				byte[] newBuffer = new byte[chunkSize];
				Buffer.BlockCopy (head, 0, newBuffer, 0, head.Length);
				Buffer.BlockCopy (buffer, offset, newBuffer, head.Length, size);
				Buffer.BlockCopy (crlf, 0, newBuffer, head.Length + size, crlf.Length);

				if (allowBuffering) {
					if (writeBuffer == null)
						writeBuffer = new MemoryStream ();
					writeBuffer.Write (buffer, offset, size);
					totalWritten += size;
				}

				buffer = newBuffer;
				offset = 0;
				size = chunkSize;
			} else {
				CheckWriteOverflow (request.ContentLength, totalWritten, size);

				if (allowBuffering) {
					if (writeBuffer == null)
						writeBuffer = new MemoryStream ();
					writeBuffer.Write (buffer, offset, size);
					totalWritten += size;

					if (request.ContentLength <= 0 || totalWritten < request.ContentLength) {
						result.SetCompleted (true, 0);
						result.DoCallback ();
						return result;
					}

					result.AsyncWriteAll = true;
					requestWritten = true;
					buffer = writeBuffer.GetBuffer ();
					offset = 0;
					size = (int)totalWritten;
				}
			}

			try {
				result.InnerAsyncResult = cnc.AsyncWrite (request, buffer, offset, size, true, callback, result);
				if (result.InnerAsyncResult == null) {
					if (!result.IsCompleted)
						result.SetCompleted (true, 0);
					result.DoCallback ();
				}
			} catch (Exception) {
				if (!IgnoreIOErrors)
					throw;
				result.SetCompleted (true, 0);
				result.DoCallback ();
			}
			totalWritten += size;
			return result;
		}

		void CheckWriteOverflow (long contentLength, long totalWritten, long size)
		{
			if (contentLength == -1)
				return;

			long avail = contentLength - totalWritten;
			if (size > avail) {
				KillBuffer ();
				nextReadCalled = true;
				cnc.Close (true);
				throw new ProtocolViolationException (
					"The number of bytes to be written is greater than " +
					"the specified ContentLength.");
			}
		}

		public override void EndWrite (IAsyncResult r)
		{
			if (r == null)
				throw new ArgumentNullException ("r");

			TaskToApm.End (r);
			return;

			WebAsyncResult result = r as WebAsyncResult;
			if (result == null)
				throw new ArgumentException ("Invalid IAsyncResult");

			if (result.EndCalled)
				return;

			if (sendChunked) {
				lock (locker) {
					pendingWrites--;
					if (pendingWrites <= 0)
						pending.Set ();
				}
			}

			result.EndCalled = true;
			if (result.AsyncWriteAll) {
				result.WaitUntilComplete ();
				if (result.GotException)
					throw result.Exception;
				return;
			}

			if (allowBuffering && !sendChunked)
				return;

			if (result.GotException)
				throw result.Exception;
		}

		public override void Write (byte[] buffer, int offset, int size)
		{
			WriteAsync (buffer, offset, size).Wait ();
			return;

			AsyncCallback cb = cb_wrapper;
			WebAsyncResult res = (WebAsyncResult)BeginWrite (buffer, offset, size, cb, null);
			if (!res.IsCompleted && !res.WaitUntilComplete (WriteTimeout, false)) {
				KillBuffer ();
				nextReadCalled = true;
				cnc.Close (true);
				throw new IOException ("Write timed out.");
			}

			EndWrite (res);
		}

		public override void Flush ()
		{
		}

		internal async Task SetHeadersAsync (bool setInternalLength, CancellationToken cancellationToken)
		{
			if (headersSent)
				return;

			string method = request.Method;
			bool no_writestream = (method == "GET" || method == "CONNECT" || method == "HEAD" ||
					      method == "TRACE");
			bool webdav = (method == "PROPFIND" || method == "PROPPATCH" || method == "MKCOL" ||
				      method == "COPY" || method == "MOVE" || method == "LOCK" ||
				      method == "UNLOCK");

			if (setInternalLength && !no_writestream && writeBuffer != null)
				request.InternalContentLength = writeBuffer.Length;

			bool has_content = !no_writestream && (writeBuffer == null || request.ContentLength > -1);
			if (!(sendChunked || has_content || no_writestream || webdav))
				return;

			headersSent = true;
			headers = request.GetRequestHeaders ();

			try {
				await cnc.WriteAsync (request, headers, 0, headers.Length, cancellationToken).ConfigureAwait (false);
				if (!initRead) {
					initRead = true;
					cnc.InitRead ();
				}
				var cl = request.ContentLength;
				if (!sendChunked && cl == 0)
					requestWritten = true;
			} catch (Exception e) {
				if (e is WebException || e is OperationCanceledException)
					throw;
				throw new WebException ("Error writing headers", WebExceptionStatus.SendFailure, WebExceptionInternalStatus.RequestFatal, e);
			}
		}

		internal async Task WriteRequestAsync (CancellationToken cancellationToken)
		{
			if (requestWritten)
				return;

			requestWritten = true;
			if (sendChunked || !allowBuffering || writeBuffer == null)
				return;

			// Keep the call for a potential side-effect of GetBuffer
			var bytes = writeBuffer.GetBuffer ();
			var length = (int)writeBuffer.Length;
			if (request.ContentLength != -1 && request.ContentLength < length) {
				nextReadCalled = true;
				cnc.Close (true);
				throw new WebException ("Specified Content-Length is less than the number of bytes to write", null,
					WebExceptionStatus.ServerProtocolViolation, null);
			}

			await SetHeadersAsync (true, cancellationToken).ConfigureAwait (false);

			if (cnc.Data.StatusCode != 0 && cnc.Data.StatusCode != 100)
				return;

			if (!initRead) {
				initRead = true;
				cnc.InitRead ();
			}

			if (length == 0) {
				complete_request_written = true;
				return;
			}

			await cnc.WriteAsync (request, bytes, 0, length, cancellationToken).ConfigureAwait (false);
			complete_request_written = true;
		}

		internal bool RequestWritten {
			get { return requestWritten; }
		}

		internal void InternalClose ()
		{
			disposed = true;
		}

		async Task WriteChunkTrailer ()
		{
			using (var cts = new CancellationTokenSource ()) {
				cts.CancelAfter (WriteTimeout);
				var timeoutTask = Task.Delay (WriteTimeout);
				while (true) {
					var myWriteTcs = new TaskCompletionSource<int> ();
					var oldTcs = Interlocked.CompareExchange (ref pendingWrite, myWriteTcs, null);
					if (oldTcs == null)
						break;
					var ret = await Task.WhenAny (timeoutTask, oldTcs.Task).ConfigureAwait (false);
					if (ret == timeoutTask)
						throw new WebException ("The operation has timed out.", WebExceptionStatus.Timeout);
				}

				try {
					byte[] chunk = Encoding.ASCII.GetBytes ("0\r\n\r\n");
					await cnc.WriteAsync (request, chunk, 0, chunk.Length, cts.Token).ConfigureAwait (false);
				} catch {
					;
				} finally {
					pendingWrite = null;
				}
			}
		}

		public override void Close ()
		{
			if (sendChunked) {
				if (disposed)
					return;
				disposed = true;
				WriteChunkTrailer ().Wait ();
				return;
			}

			if (isRead) {
				if (!nextReadCalled) {
					CheckComplete ();
					// If we have not read all the contents
					if (!nextReadCalled) {
						nextReadCalled = true;
						cnc.Close (true);
					}
				}
				return;
			} else if (!allowBuffering) {
				complete_request_written = true;
				if (!initRead) {
					initRead = true;
					cnc.InitRead ();
				}
				return;
			}

			if (disposed || requestWritten)
				return;

			long length = request.ContentLength;

			if (!sendChunked && length != -1 && totalWritten != length) {
				IOException io = new IOException ("Cannot close the stream until all bytes are written");
				nextReadCalled = true;
				cnc.Close (true);
				throw new WebException ("Request was cancelled.", WebExceptionStatus.RequestCanceled, WebExceptionInternalStatus.RequestFatal, io);
			}

			// Commented out the next line to fix xamarin bug #1512
			//WriteRequest ();
			disposed = true;
		}

		internal void KillBuffer ()
		{
			writeBuffer = null;
		}

		public override long Seek (long a, SeekOrigin b)
		{
			throw new NotSupportedException ();
		}

		public override void SetLength (long a)
		{
			throw new NotSupportedException ();
		}

		public override bool CanSeek {
			get { return false; }
		}

		public override bool CanRead {
			get { return !disposed && isRead; }
		}

		public override bool CanWrite {
			get { return !disposed && !isRead; }
		}

		public override long Length {
			get {
				if (!isRead)
					throw new NotSupportedException ();
				return stream_length;
			}
		}

		public override long Position {
			get { throw new NotSupportedException (); }
			set { throw new NotSupportedException (); }
		}
	}
}

