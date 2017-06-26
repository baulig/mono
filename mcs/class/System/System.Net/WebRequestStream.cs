#define MARTIN_DEBUG
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Net.Sockets;

namespace System.Net
{
	class WebRequestStream : WebConnectionStream
	{
		static byte[] crlf = new byte[] { 13, 10 };
		MemoryStream writeBuffer;
		bool requestWritten;
		bool allowBuffering;
		bool sendChunked;
		TaskCompletionSource<int> pendingWrite;
		byte[] headers;
		bool headersSent;

		public WebRequestStream (WebConnection connection, WebOperation operation, WebConnectionData data)
			: base (connection, operation, data, operation.Request)
		{
			allowBuffering = operation.Request.InternalAllowBuffering;
			sendChunked = operation.Request.SendChunked && operation.WriteBuffer == null;
			if (!sendChunked && allowBuffering && operation.WriteBuffer == null)
				writeBuffer = new MemoryStream ();
		}

		internal bool SendChunked {
			get { return sendChunked; }
			set { sendChunked = value; }
		}

		internal bool HasWriteBuffer {
			get {
				return Operation.WriteBuffer != null || writeBuffer != null;
			}
		}

		internal int WriteBufferLength {
			get {
				if (Operation.WriteBuffer != null)
					return Operation.WriteBuffer.Size;
				if (writeBuffer != null)
					return (int)writeBuffer.Length;
				return -1;
			}
		}

		internal BufferOffsetSize GetWriteBuffer ()
		{
			if (Operation.WriteBuffer != null)
				return Operation.WriteBuffer;
			if (writeBuffer == null || writeBuffer.Length == 0)
				return null;
			var buffer = writeBuffer.GetBuffer ();
			return new BufferOffsetSize (buffer, 0, (int)writeBuffer.Length, false);
		}

		internal bool RequestWritten {
			get { return requestWritten; }
		}

		public override async Task WriteAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			// WebConnection.Debug ($"WCS WRITE ASYNC: {Connection.ID}");

			Operation.ThrowIfClosedOrDisposed (cancellationToken);

			if (Operation.WriteBuffer != null)
				throw new InvalidOperationException ();

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

				if (allowBuffering && !sendChunked && Request.ContentLength > 0 && totalWritten == Request.ContentLength)
					Operation.CompleteRequestWritten (this);

				pendingWrite = null;
				myWriteTcs.TrySetResult (0);
			} catch (Exception ex) {
				KillBuffer ();
				closed = true;
				Connection.CloseError ();

				if (ex is SocketException)
					ex = new IOException ("Error writing request", ex);

				Operation.CompleteRequestWritten (this, ex);

				pendingWrite = null;
				myWriteTcs.TrySetException (ex);
				throw;
			}

		}

		async Task ProcessWrite (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			Operation.ThrowIfClosedOrDisposed (cancellationToken);

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
				CheckWriteOverflow (Request.ContentLength, totalWritten, size);

				if (allowBuffering) {
					if (writeBuffer == null)
						writeBuffer = new MemoryStream ();
					writeBuffer.Write (buffer, offset, size);
					totalWritten += size;

					if (Request.ContentLength <= 0 || totalWritten < Request.ContentLength)
						return;

					requestWritten = true;
					buffer = writeBuffer.GetBuffer ();
					offset = 0;
					size = (int)totalWritten;
				}
			}

			try {
				await Data.NetworkStream.WriteAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch {
				if (!IgnoreIOErrors)
					throw;
			}
			totalWritten += size;
		}

		void CheckWriteOverflow (long contentLength, long totalWritten, long size)
		{
			if (contentLength == -1)
				return;

			long avail = contentLength - totalWritten;
			if (size > avail) {
				KillBuffer ();
				closed = true;
				Connection.CloseError ();
				throw new ProtocolViolationException (
					"The number of bytes to be written is greater than " +
					"the specified ContentLength.");
			}
		}

		internal async Task Initialize (CancellationToken cancellationToken)
		{
			Operation.ThrowIfClosedOrDisposed (cancellationToken);

			if (Operation.WriteBuffer != null) {
				if (Operation.IsNtlmChallenge)
					Request.InternalContentLength = 0;
				else
					Request.InternalContentLength = Operation.WriteBuffer.Size;
			}

			await SetHeadersAsync (false, cancellationToken).ConfigureAwait (false);

			Operation.ThrowIfClosedOrDisposed (cancellationToken);

			if (Operation.WriteBuffer != null && !Operation.IsNtlmChallenge) {
				await WriteRequestAsync (cancellationToken);
				Close ();
			}
		}

		async Task SetHeadersAsync (bool setInternalLength, CancellationToken cancellationToken)
		{
			Operation.ThrowIfClosedOrDisposed (cancellationToken);

			if (headersSent)
				return;

			string method = Request.Method;
			bool no_writestream = (method == "GET" || method == "CONNECT" || method == "HEAD" ||
					      method == "TRACE");
			bool webdav = (method == "PROPFIND" || method == "PROPPATCH" || method == "MKCOL" ||
				      method == "COPY" || method == "MOVE" || method == "LOCK" ||
				      method == "UNLOCK");

			if (Operation.IsNtlmChallenge)
				no_writestream = true;

			if (setInternalLength && !no_writestream && HasWriteBuffer)
				Request.InternalContentLength = WriteBufferLength;

			bool has_content = !no_writestream && (!HasWriteBuffer || Request.ContentLength > -1);
			if (!(sendChunked || has_content || no_writestream || webdav))
				return;

			headersSent = true;
			headers = Request.GetRequestHeaders ();

			try {
				await Data.NetworkStream.WriteAsync (headers, 0, headers.Length, cancellationToken).ConfigureAwait (false);
				var cl = Request.ContentLength;
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
			Operation.ThrowIfClosedOrDisposed (cancellationToken);

			if (requestWritten)
				return;

			requestWritten = true;
			if (sendChunked || !allowBuffering || !HasWriteBuffer)
				return;

			BufferOffsetSize buffer = GetWriteBuffer ();
			if (buffer != null && !Operation.IsNtlmChallenge && Request.ContentLength != -1 && Request.ContentLength < buffer.Size) {
				closed = true;
				Connection.CloseError ();
				throw new WebException ("Specified Content-Length is less than the number of bytes to write", null,
					WebExceptionStatus.ServerProtocolViolation, null);
			}

			await SetHeadersAsync (true, cancellationToken).ConfigureAwait (false);

			if (Data.StatusCode != 0 && Data.StatusCode != 100)
				return;

			if (buffer != null) {
				if (buffer.Size == 0) {
					Operation.CompleteRequestWritten (this);
					return;
				}

				await Data.NetworkStream.WriteAsync (buffer.Buffer, 0, buffer.Size, cancellationToken).ConfigureAwait (false);
				Operation.CompleteRequestWritten (this);
			}
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
					Operation.ThrowIfClosedOrDisposed (cts.Token);
					byte[] chunk = Encoding.ASCII.GetBytes ("0\r\n\r\n");
					await Data.NetworkStream.WriteAsync (chunk, 0, chunk.Length, cts.Token).ConfigureAwait (false);
				} catch {
					;
				} finally {
					pendingWrite = null;
				}
			}
		}

		internal void KillBuffer ()
		{
			writeBuffer = null;
		}

		protected override void Close_internal (ref bool disposed)
		{
			if (sendChunked) {
				if (disposed)
					return;
				disposed = true;
				WriteChunkTrailer ().Wait ();
				return;
			}

			if (!allowBuffering) {
				Operation.CompleteRequestWritten (this);
				return;
			}

			if (disposed || requestWritten)
				return;

			long length = Request.ContentLength;

			if (!sendChunked && !Operation.IsNtlmChallenge && length != -1 && totalWritten != length) {
				IOException io = new IOException ("Cannot close the stream until all bytes are written");
				closed = true;
				Connection.CloseError();
				throw new WebException ("Request was cancelled.", WebExceptionStatus.RequestCanceled, WebExceptionInternalStatus.RequestFatal, io);
			}

			// Commented out the next line to fix xamarin bug #1512
			//WriteRequest ();
			disposed = true;
		}
	}
}
