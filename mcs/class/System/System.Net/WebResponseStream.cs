#define MARTIN_DEBUG
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Net.Sockets;

namespace System.Net
{
	class WebResponseStream : WebConnectionStream
	{
		byte[] readBuffer;
		int readBufferOffset;
		int readBufferSize;
		long contentLength;
		long totalRead;
		bool nextReadCalled;
		int stream_length; // -1 when CL not present
		TaskCompletionSource<int> readTcs;
		object locker = new object ();
		int nestedRead;
		bool read_eof;

		public WebResponseStream (WebConnection connection, WebOperation operation, WebConnectionData data)
			: base (connection, operation, data)
		{
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

		public override long Length {
			get {
				return stream_length;
			}
		}

		public override async Task<int> ReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS READ ASYNC: {Connection.ID}");

			cancellationToken.ThrowIfCancellationRequested ();

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
				WebConnection.Debug ($"WCS READ ASYNC #1: {Connection.ID} {oldReadTcs != null}");
				if (oldReadTcs == null)
					break;
				await oldReadTcs.Task.ConfigureAwait (false);
			}

			WebConnection.Debug ($"WCS READ ASYNC #2: {Connection.ID} {totalRead} {contentLength}");

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
				Connection.CloseError ();
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
				WebConnection.Debug ($"WCS READ ASYNC - READ ALL: {Connection.ID} {oldBytes} {nbytes}");
				await ReadAllAsync (cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WCS READ ASYNC - READ ALL DONE: {Connection.ID} {oldBytes} {nbytes}");
			}

			return oldBytes + nbytes;
		}

		async Task<(int, int)> ProcessRead (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS PROCESS READ: {Connection.ID} {totalRead} {contentLength}");

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

			WebConnection.Debug ($"WCS READ ASYNC #1: {Connection.ID} {oldBytes} {size} {read_eof}");

			if (read_eof)
				return (oldBytes, 0);

			var ret = await InnerReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			return (oldBytes, ret);
		}

		internal async Task<int> InnerReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP READ ASYNC: {Connection.ID}");

			Operation.ThrowIfDisposed (cancellationToken);
			var s = Data.NetworkStream;
			if (s == null)
				throw new ObjectDisposedException (typeof (NetworkStream).FullName);

			int nbytes = 0;
			bool done = false;

			if (!Data.ChunkedRead || (!Data.ChunkStream.DataAvailable && Data.ChunkStream.WantMore)) {
				nbytes = await s.ReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WC READ ASYNC #1: {Connection.ID} {nbytes} {Data.ChunkedRead}");
				if (!Data.ChunkedRead)
					return nbytes;
				done = nbytes == 0;
			}

			try {
				Data.ChunkStream.WriteAndReadBack (buffer, offset, size, ref nbytes);
				WebConnection.Debug ($"WC READ ASYNC #1: {Connection.ID} {done} {nbytes} {Data.ChunkStream.WantMore}");
				if (!done && nbytes == 0 && Data.ChunkStream.WantMore)
					nbytes = await EnsureReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				if (e is WebException || e is OperationCanceledException)
					throw;
				throw new WebException ("Invalid chunked data.", e, WebExceptionStatus.ServerProtocolViolation, null);
			}

			if ((done || nbytes == 0) && Data.ChunkStream.ChunkLeft != 0) {
				// HandleError (WebExceptionStatus.ReceiveFailure, null, "chunked EndRead");
				throw new WebException ("Read error", null, WebExceptionStatus.ReceiveFailure, null);
			}

			return nbytes;
		}

		async Task<int> EnsureReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			byte[] morebytes = null;
			int nbytes = 0;
			while (nbytes == 0 && Data.ChunkStream.WantMore && !cancellationToken.IsCancellationRequested) {
				int localsize = Data.ChunkStream.ChunkLeft;
				if (localsize <= 0) // not read chunk size yet
					localsize = 1024;
				else if (localsize > 16384)
					localsize = 16384;

				if (morebytes == null || morebytes.Length < localsize)
					morebytes = new byte[localsize];

				int nread = await Data.NetworkStream.ReadAsync (morebytes, 0, localsize, cancellationToken).ConfigureAwait (false);
				if (nread <= 0)
					return 0; // Error

				Data.ChunkStream.Write (morebytes, 0, nread);
				nbytes += Data.ChunkStream.Read (buffer, offset + nbytes, size - nbytes);
			}

			return nbytes;
		}

		bool CheckAuthHeader (string headerName)
		{
			var authHeader = Data.Headers[headerName];
			return (authHeader != null && authHeader.IndexOf ("NTLM", StringComparison.Ordinal) != -1);
		}

		bool IsNtlmAuth ()
		{
			bool isProxy = (Request.Proxy != null && !Request.Proxy.IsBypassed (Request.Address));
			if (isProxy && CheckAuthHeader ("Proxy-Authenticate"))
				return true;
			return CheckAuthHeader ("WWW-Authenticate");
		}

		async Task CheckResponseInBuffer (CancellationToken cancellationToken)
		{
			if (contentLength > 0 && (readBufferSize - readBufferOffset) >= contentLength) {
				if (!IsNtlmAuth ())
					await ReadAllAsync (cancellationToken).ConfigureAwait (false);
			}
		}

		static bool ExpectContent (int statusCode, string method)
		{
			if (method == "HEAD")
				return false;
			return (statusCode >= 200 && statusCode != 204 && statusCode != 304);
		}

		internal async Task Initialize (BufferOffsetSize buffer, CancellationToken cancellationToken)
		{
			string me = "WebResponseStream.Initialize()";
			bool expect_content = ExpectContent (Data.StatusCode, Data.Request.Method);
			string tencoding = null;
			if (expect_content)
				tencoding = Data.Headers["Transfer-Encoding"];

			Data.ChunkedRead = (tencoding != null && tencoding.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			if (!Data.ChunkedRead) {
				readBuffer = buffer.Buffer;
				readBufferOffset = buffer.Offset;
				readBufferSize = buffer.Size;
				try {
					if (contentLength > 0 && (readBufferSize - readBufferOffset) >= contentLength) {
						if (!IsNtlmAuth ())
							await ReadAllAsync (cancellationToken).ConfigureAwait (false);
					}
				} catch (Exception e) {
					throw WebConnection.GetReadException (WebExceptionStatus.ReceiveFailure, e, me);
				}
			} else if (Data.ChunkStream == null) {
				try {
					Data.ChunkStream = new MonoChunkStream (buffer.Buffer, buffer.Offset, buffer.Size, Data.Headers);
				} catch (Exception e) {
					throw WebConnection.GetReadException (WebExceptionStatus.ServerProtocolViolation, e, me);
				}
			} else {
				Data.ChunkStream.ResetBuffer ();
				try {
					Data.ChunkStream.Write (buffer.Buffer, buffer.Offset, buffer.Size);
				} catch (Exception e) {
					throw WebConnection.GetReadException (WebExceptionStatus.ServerProtocolViolation, e, me);
				}
			}

			if (!expect_content) {
				Operation.CompleteResponseRead (this, true);
				if (!closed && !nextReadCalled) {
					if (contentLength == Int64.MaxValue)
						contentLength = 0;
					nextReadCalled = true;
					Connection.NextRead ();
				}
			}
		}

		internal async Task ReadAllAsync (CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WCS READ ALL ASYNC: {Connection.ID}");
			if (read_eof || totalRead >= contentLength || nextReadCalled) {
				if (!nextReadCalled) {
					nextReadCalled = true;
					Operation.CompleteResponseRead (this, true);
					Connection.NextRead ();
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

			WebConnection.Debug ($"WCS READ ALL ASYNC #1: {Connection.ID}");

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
					while ((read = await InnerReadAsync (buffer, 0, buffer.Length, cancellationToken)) != 0)
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
						r = await InnerReadAsync (b, diff, remaining, cancellationToken);
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
				WebConnection.Debug ($"WCS READ ALL ASYNC EX: {Connection.ID} {ex.Message}");
				myReadTcs.TrySetException (ex);
				throw;
			} finally {
				WebConnection.Debug ($"WCS READ ALL #2: {Connection.ID}");
				readTcs = null;
			}

			Operation.CompleteResponseRead (this, true);
			Connection.NextRead ();
		}

		protected override void Close_internal (ref bool disposed)
		{
			if (!closed && !nextReadCalled) {
				nextReadCalled = true;
				if (readBufferSize - readBufferOffset == contentLength) {
					Operation.CompleteResponseRead (this, true);
					Connection.NextRead ();
				} else {
					// If we have not read all the contents
					closed = true;
					Operation.CompleteResponseRead (this, false);
					Connection.CloseError ();
				}
			}
		}
	}
}
