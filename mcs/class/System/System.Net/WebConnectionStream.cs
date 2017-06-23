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

	abstract class WebConnectionStream : Stream
	{
		bool isRead;
		WebConnection cnc;
		WebOperation operation;
		WebConnectionData data;
		HttpWebRequest request;
		byte[] readBuffer;
		int readBufferOffset;
		int readBufferSize;
		int stream_length; // -1 when CL not present
		long contentLength;
		long totalRead;
		internal long totalWritten;
		bool nextReadCalled;
		protected bool closed;
		bool disposed;
		object locker = new object ();
		TaskCompletionSource<int> readTcs;
		int nestedRead;
		bool read_eof;
		int read_timeout;
		int write_timeout;
		internal bool IgnoreIOErrors;

		public WebConnectionStream (WebConnection cnc, WebOperation operation, WebConnectionData data)
		{
			if (data == null)
				throw new InvalidOperationException ("data was not initialized");
			if (data.Headers == null)
				throw new InvalidOperationException ("data.Headers was not initialized");
			if (data.Request == null)
				throw new InvalidOperationException ("data.Request was not initialized");
			isRead = true;
			this.operation = operation;
			this.data = data;
			this.request = data.Request;
			read_timeout = request.ReadWriteTimeout;
			write_timeout = read_timeout;
			this.cnc = cnc;
			string contentType = data.Headers["Transfer-Encoding"];
			bool chunkedRead = (contentType != null && contentType.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			string clength = data.Headers["Content-Length"];
			if (!chunkedRead && !string.IsNullOrEmpty (clength)) {
				if (!long.TryParse (clength, out contentLength))
					contentLength = Int64.MaxValue;
			} else {
				contentLength = Int64.MaxValue;
			}

			// Negative numbers?
			if (!Int32.TryParse (clength, out stream_length))
				stream_length = -1;
		}

		public WebConnectionStream (WebConnection cnc, WebOperation operation, WebConnectionData data, HttpWebRequest request)
		{
			read_timeout = request.ReadWriteTimeout;
			write_timeout = read_timeout;
			isRead = false;
			this.cnc = cnc;
			this.operation = operation;
			this.data = data;
			this.request = request;
		}

		bool CheckAuthHeader (string headerName)
		{
			var authHeader = data.Headers[headerName];
			return (authHeader != null && authHeader.IndexOf ("NTLM", StringComparison.Ordinal) != -1);
		}

		bool IsNtlmAuth ()
		{
			bool isProxy = (request.Proxy != null && !request.Proxy.IsBypassed (request.Address));
			if (isProxy && CheckAuthHeader ("Proxy-Authenticate"))
				return true;
			return CheckAuthHeader ("WWW-Authenticate");
		}

		internal async Task CheckResponseInBuffer (CancellationToken cancellationToken)
		{
			if (contentLength > 0 && (readBufferSize - readBufferOffset) >= contentLength) {
				if (!IsNtlmAuth ())
					await ReadAllAsync (cancellationToken).ConfigureAwait (false);
			}
		}

		internal HttpWebRequest Request {
			get { return request; }
		}

		internal WebConnection Connection {
			get { return cnc; }
		}
		internal WebOperation Operation {
			get { return operation; }
		}
		internal WebConnectionData Data {
			get { return data; }
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

		internal byte[] ReadBuffer {
			set { readBuffer = value; }
		}

		internal int ReadBufferOffset {
			set { readBufferOffset = value; }
		}

		internal int ReadBufferSize {
			set { readBufferSize = value; }
		}

		internal bool ForceCompletion ()
		{
			if (!closed && !nextReadCalled) {
				if (contentLength == Int64.MaxValue)
					contentLength = 0;
				nextReadCalled = true;
				return true;
			}
			return false;
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
					while ((read = await cnc.ReadAsync (operation, data, buffer, 0, buffer.Length, cancellationToken)) != 0)
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
						r = await cnc.ReadAsync (operation, data, b, diff, remaining, cancellationToken);
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

		public override int Read (byte[] buffer, int offset, int size)
		{
			WebConnection.Debug ($"WCS READ: {cnc.ID}");
			try {
				return ReadAsync (buffer, offset, size, CancellationToken.None).Result;
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
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

			int oldBytes = 0, nbytes = 0;
			Exception throwMe = null;

			try {
				(oldBytes, nbytes) = await ProcessRead (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				throwMe = HttpWebRequest.FlattenException (e);
			}

			if (throwMe != null) {
				lock (locker) {
					myReadTcs.TrySetException (throwMe);
					readTcs = null;
					nestedRead = 0;
				}

				closed = true;
				cnc.CloseError ();
				throw throwMe;
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
			}

			if (totalRead >= contentLength && !nextReadCalled) {
				WebConnection.Debug ($"WCS READ ASYNC - READ ALL: {cnc.ID} {oldBytes} {nbytes}");
				await ReadAllAsync (cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WCS READ ASYNC - READ ALL DONE: {cnc.ID} {oldBytes} {nbytes}");
			}

			return oldBytes + nbytes;
		}

		async Task<(int,int)> ProcessRead (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS PROCESS READ: {cnc.ID} {totalRead} {contentLength}");

			cancellationToken.ThrowIfCancellationRequested ();
			if (totalRead >= contentLength || cancellationToken.IsCancellationRequested)
				return (0, -1);

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
					return (0, copy);
				oldBytes = copy;
			}

			if (contentLength != Int64.MaxValue && contentLength - totalRead < size)
				size = (int)(contentLength - totalRead);

			WebConnection.Debug ($"WCS READ ASYNC #1: {cnc.ID} {oldBytes} {size} {read_eof}");

			if (read_eof)
				return (oldBytes, 0);

			var ret = await cnc.ReadAsync (operation, data, buffer, offset, size, cancellationToken).ConfigureAwait (false);
			return (oldBytes, ret);
		}

		public override IAsyncResult BeginRead (byte[] buffer, int offset, int size,
							AsyncCallback cb, object state)
		{
			// WebConnection.Debug ($"WCS BEGIN READ: {cnc.ID}");

			var task = ReadAsync (buffer, offset, size, CancellationToken.None);
			return TaskToApm.Begin (task, cb, state);
		}

		public override int EndRead (IAsyncResult r)
		{
			// WebConnection.Debug ($"WCS END READ: {cnc.ID}");
			try {
				return TaskToApm.End<int> (r);
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}


		public override IAsyncResult BeginWrite (byte[] buffer, int offset, int size,
							 AsyncCallback cb, object state)
		{
			if (request.Aborted)
				throw new WebException ("The request was canceled.", WebExceptionStatus.RequestCanceled);

			var task = WriteAsync (buffer, offset, size, CancellationToken.None);
			return TaskToApm.Begin (task, cb, state);
		}

		public override void EndWrite (IAsyncResult r)
		{
			if (r == null)
				throw new ArgumentNullException ("r");

			try {
				TaskToApm.End (r);
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}

		public override void Write (byte[] buffer, int offset, int size)
		{
			try {
				WriteAsync (buffer, offset, size).Wait ();
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}

		public override void Flush ()
		{
		}

		internal void InternalClose ()
		{
			disposed = true;
		}

		protected abstract void Close_internal (ref bool disposed);

		public override void Close ()
		{
			Close_internal (ref disposed);

			if (isRead) {
				if (!closed && !nextReadCalled) {
					nextReadCalled = true;
					if (readBufferSize - readBufferOffset == contentLength) {
						cnc.NextRead ();
					} else {
						// If we have not read all the contents
						closed = true;
						cnc.CloseError ();
					}
				}
				return;
			}

			disposed = true;
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

