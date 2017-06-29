#define MARTIN_DEBUG
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
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

		public WebRequestStream RequestStream {
			get;
		}

		public WebHeaderCollection Headers {
			get;
			private set;
		}

		public HttpStatusCode StatusCode {
			get;
			private set;
		}

		public string StatusDescription {
			get;
			private set;
		}

		public Version Version {
			get;
			private set;
		}

		public bool KeepAlive {
			get;
			private set;
		}

		public WebResponseStream (WebRequestStream request, Stream stream)
			: base (request.Connection, request.Operation, stream)
		{
			RequestStream = request;
		}

		public override long Length {
			get {
				return stream_length;
			}
		}

		public override bool CanRead => true;

		public override bool CanWrite => false;

		protected bool ChunkedRead {
			get;
			private set;
		}

		protected MonoChunkStream ChunkStream {
			get;
			private set;
		}

		public override async Task<int> ReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP READ ASYNC: Cnc={Connection.ID}");

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
				WebConnection.Debug ($"WRP READ ASYNC #1: Cnc={Connection.ID} {oldReadTcs != null}");
				if (oldReadTcs == null)
					break;
				await oldReadTcs.Task.ConfigureAwait (false);
			}

			WebConnection.Debug ($"WRP READ ASYNC #2: Cnc={Connection.ID} {totalRead} {contentLength}");

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
				Operation.CompleteResponseRead (this, false, throwMe);
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
				WebConnection.Debug ($"WRP READ ASYNC - READ ALL: Cnc={Connection.ID} {oldBytes} {nbytes}");
				await ReadAllAsync (cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WRP READ ASYNC - READ ALL DONE: Cnc={Connection.ID} {oldBytes} {nbytes}");
			}

			return oldBytes + nbytes;
		}

		async Task<(int, int)> ProcessRead (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP PROCESS READ: Cnc={Connection.ID} {totalRead} {contentLength}");

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

			WebConnection.Debug ($"WRP READ ASYNC #1: Cnc={Connection.ID} {oldBytes} {size} {read_eof}");

			if (read_eof)
				return (oldBytes, 0);

			var ret = await InnerReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			return (oldBytes, ret);
		}

		internal async Task<int> InnerReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP READ ASYNC: Cnc={Connection.ID}");

			Operation.ThrowIfDisposed (cancellationToken);

			int nbytes = 0;
			bool done = false;

			if (!ChunkedRead || (!ChunkStream.DataAvailable && ChunkStream.WantMore)) {
				nbytes = await InnerStream.ReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WRP READ ASYNC #1: Cnc={Connection.ID} {nbytes} {ChunkedRead}");
				if (!ChunkedRead)
					return nbytes;
				done = nbytes == 0;
			}

			try {
				ChunkStream.WriteAndReadBack (buffer, offset, size, ref nbytes);
				WebConnection.Debug ($"WRP READ ASYNC #1: Cnc={Connection.ID} {done} {nbytes} {ChunkStream.WantMore}");
				if (!done && nbytes == 0 && ChunkStream.WantMore)
					nbytes = await EnsureReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				if (e is WebException || e is OperationCanceledException)
					throw;
				throw new WebException ("Invalid chunked data.", e, WebExceptionStatus.ServerProtocolViolation, null);
			}

			if ((done || nbytes == 0) && ChunkStream.ChunkLeft != 0) {
				// HandleError (WebExceptionStatus.ReceiveFailure, null, "chunked EndRead");
				throw new WebException ("Read error", null, WebExceptionStatus.ReceiveFailure, null);
			}

			return nbytes;
		}

		async Task<int> EnsureReadAsync (byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			byte[] morebytes = null;
			int nbytes = 0;
			while (nbytes == 0 && ChunkStream.WantMore && !cancellationToken.IsCancellationRequested) {
				int localsize = ChunkStream.ChunkLeft;
				if (localsize <= 0) // not read chunk size yet
					localsize = 1024;
				else if (localsize > 16384)
					localsize = 16384;

				if (morebytes == null || morebytes.Length < localsize)
					morebytes = new byte[localsize];

				int nread = await InnerStream.ReadAsync (morebytes, 0, localsize, cancellationToken).ConfigureAwait (false);
				if (nread <= 0)
					return 0; // Error

				ChunkStream.Write (morebytes, 0, nread);
				nbytes += ChunkStream.Read (buffer, offset + nbytes, size - nbytes);
			}

			return nbytes;
		}

		bool CheckAuthHeader (string headerName)
		{
			var authHeader = Headers[headerName];
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

		bool ExpectContent {
			get {
				if (Request.Method == "HEAD")
					return false;
				return ((int)StatusCode >= 200 && (int)StatusCode != 204 && (int)StatusCode != 304);
			}
		}

		async Task Initialize (BufferOffsetSize buffer, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP INIT: Cnc={Connection.ID} status={(int)StatusCode} bos={buffer.Offset}/{buffer.Size}");

			string contentType = Headers["Transfer-Encoding"];
			bool chunkedRead = (contentType != null && contentType.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			string clength = Headers["Content-Length"];
			if (!chunkedRead && !string.IsNullOrEmpty (clength)) {
				if (!long.TryParse (clength, out contentLength))
					contentLength = Int64.MaxValue;
			} else {
				contentLength = Int64.MaxValue;
			}

			if (Version == HttpVersion.Version11 && RequestStream.KeepAlive) {
				KeepAlive = true;
				var cncHeader = Headers[ServicePoint.UsesProxy ? "Proxy-Connection" : "Connection"];
				if (cncHeader != null) {
					cncHeader = cncHeader.ToLower ();
					KeepAlive = cncHeader.IndexOf ("keep-alive", StringComparison.Ordinal) != -1;
					if (cncHeader.IndexOf ("close", StringComparison.Ordinal) != -1)
						KeepAlive = false;
				}
			}

			// Negative numbers?
			if (!Int32.TryParse (clength, out stream_length))
				stream_length = -1;

			string me = "WebResponseStream.Initialize()";
			string tencoding = null;
			if (ExpectContent)
				tencoding = Headers["Transfer-Encoding"];

			ChunkedRead = (tencoding != null && tencoding.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			if (!ChunkedRead) {
				readBuffer = buffer.Buffer;
				readBufferOffset = buffer.Offset;
				readBufferSize = buffer.Size;
				try {
					if (contentLength > 0 && (readBufferSize - readBufferOffset) >= contentLength) {
						if (!IsNtlmAuth ())
							await ReadAllAsync (cancellationToken).ConfigureAwait (false);
					}
				} catch (Exception e) {
					throw GetReadException (WebExceptionStatus.ReceiveFailure, e, me);
				}
			} else if (ChunkStream == null) {
				try {
					ChunkStream = new MonoChunkStream (buffer.Buffer, buffer.Offset, buffer.Size, Headers);
				} catch (Exception e) {
					throw GetReadException (WebExceptionStatus.ServerProtocolViolation, e, me);
				}
			} else {
				ChunkStream.ResetBuffer ();
				try {
					ChunkStream.Write (buffer.Buffer, buffer.Offset, buffer.Size);
				} catch (Exception e) {
					throw GetReadException (WebExceptionStatus.ServerProtocolViolation, e, me);
				}
			}

			WebConnection.Debug ($"WRP INIT #1: Cnc={Connection.ID} - {ExpectContent} {closed} {nextReadCalled}");

			if (!ExpectContent) {
				if (!closed && !nextReadCalled) {
					if (contentLength == Int64.MaxValue)
						contentLength = 0;
					nextReadCalled = true;
				}
				Operation.CompleteResponseRead (this, true);
			}
		}

		internal async Task ReadAllAsync (CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP READ ALL ASYNC: Cnc={Connection.ID} - {read_eof} {totalRead} {contentLength} {nextReadCalled}");
			if (read_eof || totalRead >= contentLength || nextReadCalled) {
				if (!nextReadCalled) {
					nextReadCalled = true;
					Operation.CompleteResponseRead (this, true);
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

			WebConnection.Debug ($"WRP READ ALL ASYNC #1: Cnc={Connection.ID}");

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
				WebConnection.Debug ($"WRP READ ALL ASYNC EX: Cnc={Connection.ID} {ex.Message}");
				myReadTcs.TrySetException (ex);
				throw;
			} finally {
				WebConnection.Debug ($"WRP READ ALL ASYNC #2: Cnc={Connection.ID}");
				readTcs = null;
			}

			Operation.CompleteResponseRead (this, true);
		}

		protected override void Close_internal (ref bool disposed)
		{
			if (!closed && !nextReadCalled) {
				nextReadCalled = true;
				if (readBufferSize - readBufferOffset == contentLength) {
					Operation.CompleteResponseRead (this, true);
				} else {
					// If we have not read all the contents
					closed = true;
					Operation.CompleteResponseRead (this, false);
				}
			}
		}

		internal static WebException GetReadException (WebExceptionStatus status, Exception error, string where)
		{
			string msg = $"Error getting response stream ({where}): {status}";
			if (error == null)
				return new WebException ($"Error getting response stream ({where}): {status}", status);
			if (error is WebException wex)
				return wex;
			return new WebException ($"Error getting response stream ({where}): {status} {error.Message}", status,
						 WebExceptionInternalStatus.RequestFatal, error);
		}

		internal async Task InitReadAsync (CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WRP INIT READ ASYNC: Cnc={Connection.ID} Op={Operation.ID}");

			var buffer = new BufferOffsetSize (new byte[4096], false);
			var state = ReadState.None;
			int position = 0;

			while (true) {
				Operation.ThrowIfClosedOrDisposed (cancellationToken);

				WebConnection.Debug ($"WRP INIT READ ASYNC LOOP: Cnc={Connection.ID} Op={Operation.ID} {state} - {buffer.Offset}/{buffer.Size}");

				var nread = await InnerStream.ReadAsync (
					buffer.Buffer, buffer.Offset, buffer.Size, cancellationToken).ConfigureAwait (false);

				WebConnection.Debug ($"WRP INIT READ ASYNC LOOP #1: Cnc={Connection.ID} Op={Operation.ID} {state} - {buffer.Offset}/{buffer.Size} - {nread}");

				if (nread == 0)
					throw GetReadException (WebExceptionStatus.ReceiveFailure, null, "ReadDoneAsync2");

				if (nread < 0)
					throw GetReadException (WebExceptionStatus.ServerProtocolViolation, null, "ReadDoneAsync3");

				buffer.Offset += nread;
				buffer.Size -= nread;

				if (state == ReadState.None) {
					try {
						var oldPos = position;
						if (!GetResponse (buffer, ref position, ref state))
							position = oldPos;
					} catch (Exception e) {
						throw GetReadException (WebExceptionStatus.ServerProtocolViolation, e, "ReadDoneAsync4");
					}
				}

				if (state == ReadState.Aborted)
					throw GetReadException (WebExceptionStatus.RequestCanceled, null, "ReadDoneAsync5");

				if (state == ReadState.Content) {
					buffer.Size = buffer.Offset;
					buffer.Offset = position;
					break;
				}

				int est = nread * 2;
				if (est > buffer.Size) {
					var newBuffer = new byte[est];
					Buffer.BlockCopy (buffer.Buffer, 0, newBuffer, 0, buffer.Offset);
					buffer = new BufferOffsetSize (newBuffer, buffer.Offset, newBuffer.Length - buffer.Offset, false);
				}
				state = ReadState.None;
			}

			WebConnection.Debug ($"WRP INIT READ ASYNC LOOP DONE: Cnc={Connection.ID} Op={Operation.ID} - {buffer.Offset} {buffer.Size}");

			try {
				Operation.ThrowIfDisposed (cancellationToken);
				await Initialize (buffer, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				throw GetReadException (WebExceptionStatus.ReceiveFailure, e, "ReadDoneAsync6");
			}
		}

		bool GetResponse (BufferOffsetSize buffer, ref int pos, ref ReadState state)
		{
			string line = null;
			bool lineok = false;
			bool isContinue = false;
			bool emptyFirstLine = false;
			do {
				if (state == ReadState.Aborted)
					throw GetReadException (WebExceptionStatus.RequestCanceled, null, "GetResponse");

				if (state == ReadState.None) {
					lineok = WebConnection.ReadLine (buffer.Buffer, ref pos, buffer.Offset, ref line);
					if (!lineok)
						return false;

					if (line == null) {
						emptyFirstLine = true;
						continue;
					}
					emptyFirstLine = false;
					state = ReadState.Status;

					string[] parts = line.Split (' ');
					if (parts.Length < 2)
						throw GetReadException (WebExceptionStatus.ServerProtocolViolation, null, "GetResponse");

					if (String.Compare (parts[0], "HTTP/1.1", true) == 0) {
						Version = HttpVersion.Version11;
						ServicePoint.SetVersion (HttpVersion.Version11);
					} else {
						Version = HttpVersion.Version10;
						ServicePoint.SetVersion (HttpVersion.Version10);
					}

					StatusCode = (HttpStatusCode)UInt32.Parse (parts[1]);
					if (parts.Length >= 3)
						StatusDescription = String.Join (" ", parts, 2, parts.Length - 2);
					else
						StatusDescription = string.Empty;

					if (pos >= buffer.Size)
						return true;
				}

				emptyFirstLine = false;
				if (state == ReadState.Status) {
					state = ReadState.Headers;
					Headers = new WebHeaderCollection ();
					var headerList = new List<string> ();
					bool finished = false;
					while (!finished) {
						if (WebConnection.ReadLine (buffer.Buffer, ref pos, buffer.Offset, ref line) == false)
							break;

						if (line == null) {
							// Empty line: end of headers
							finished = true;
							continue;
						}

						if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) {
							int count = headerList.Count - 1;
							if (count < 0)
								break;

							string prev = headerList[count] + line;
							headerList[count] = prev;
						} else {
							headerList.Add (line);
						}
					}

					if (!finished)
						return false;

					// .NET uses ParseHeaders or ParseHeadersStrict which is much better
					foreach (string s in headerList) {

						int pos_s = s.IndexOf (':');
						if (pos_s == -1)
							throw new ArgumentException ("no colon found", "header");

						var header = s.Substring (0, pos_s);
						var value = s.Substring (pos_s + 1).Trim ();

						if (WebHeaderCollection.AllowMultiValues (header)) {
							Headers.AddInternal (header, value);
						} else {
							Headers.SetInternal (header, value);
						}
					}

					if (StatusCode == HttpStatusCode.Continue) {
						ServicePoint.SendContinue = true;
						if (pos >= buffer.Offset)
							return true;

						if (Request.ExpectContinue) {
							Request.DoContinueDelegate ((int)StatusCode, Headers);
							// Prevent double calls when getting the
							// headers in several packets.
							Request.ExpectContinue = false;
						}

						state = ReadState.None;
						isContinue = true;
					} else {
						state = ReadState.Content;
						return true;
					}
				}
			} while (emptyFirstLine || isContinue);

			throw GetReadException (WebExceptionStatus.ServerProtocolViolation, null, "GetResponse");
		}


	}
}
