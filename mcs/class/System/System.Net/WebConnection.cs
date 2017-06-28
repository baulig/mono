//
// System.Net.WebConnection
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
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
using System.Collections;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Diagnostics;
using Mono.Net.Security;

namespace System.Net
{
	enum ReadState
	{
		None,
		Status,
		Headers,
		Content,
		Aborted
	}

	class WebConnection
	{
		ServicePoint sPoint;
		object socketLock = new object ();
		WebExceptionStatus status;
		bool keepAlive;
		WebOperation priority_request;
		NetworkCredential ntlm_credentials;
		bool ntlm_authenticated;
		bool unsafe_sharing;

		WebConnectionData currentData;
		WebConnectionData Data {
			get { return currentData; }
		}

		enum NtlmAuthState
		{
			None,
			Challenge,
			Response
		}
		NtlmAuthState connect_ntlm_auth_state;
		HttpWebRequest connect_request;

#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
		[System.Runtime.InteropServices.DllImport ("__Internal")]
		static extern void xamarin_start_wwan (string uri);
#endif

		public WebConnection (WebConnectionState wcs, ServicePoint sPoint)
		{
			this.sPoint = sPoint;
			currentData = new WebConnectionData ();
		}

		[Conditional ("MARTIN_DEBUG")]
		internal static void Debug (string message, params object[] args)
		{
			Console.Error.WriteLine (string.Format (message, args));
		}

		[Conditional ("MARTIN_DEBUG")]
		internal static void Debug (string message)
		{
			Console.Error.WriteLine (message);
		}

		bool CanReuse (Socket socket)
		{
			// The real condition is !(socket.Poll (0, SelectMode.SelectRead) || socket.Available != 0)
			// but if there's data pending to read (!) we won't reuse the socket.
			return (socket.Poll (0, SelectMode.SelectRead) == false);
		}

		async Task<bool> CheckReusable (WebConnectionData data, CancellationToken cancellationToken)
		{
			if (data == null || cancellationToken.IsCancellationRequested)
				return false;

			if (data.Socket != null && data.Socket.Connected && status == WebExceptionStatus.Success) {
				// Take the chunked stream to the expected state (State.None)
				try {
					if (CanReuse (data.Socket) && await CompleteChunkedRead (data, cancellationToken).ConfigureAwait (false))
						return true;
				} catch {
					return false;
				}
			}

			if (data.Socket != null) {
				data.Socket.Close ();
				data.Socket = null;
			}

			data.ChunkStream = null;
			return false;
		}

		async Task<Socket> Connect (WebOperation operation, WebConnectionData data, CancellationToken cancellationToken)
		{
			IPHostEntry hostEntry = sPoint.HostEntry;

			if (hostEntry == null || hostEntry.AddressList.Length == 0) {
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					xamarin_start_wwan (sPoint.Address.ToString ());
					hostEntry = sPoint.HostEntry;
					if (hostEntry == null) {
#endif
				throw GetException (sPoint.UsesProxy ? WebExceptionStatus.ProxyNameResolutionFailure :
						    WebExceptionStatus.NameResolutionFailure, null);
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					}
#endif
			}

			foreach (IPAddress address in hostEntry.AddressList) {
				operation.ThrowIfDisposed (cancellationToken);

				Socket socket;
				try {
					socket = new Socket (address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
				} catch (Exception se) {
					// The Socket ctor can throw if we run out of FD's
					throw GetException (WebExceptionStatus.ConnectFailure, se);
				}
				IPEndPoint remote = new IPEndPoint (address, sPoint.Address.Port);
				socket.NoDelay = !sPoint.UseNagleAlgorithm;
				try {
					sPoint.KeepAliveSetup (socket);
				} catch {
					// Ignore. Not supported in all platforms.
				}

				if (!sPoint.CallEndPointDelegate (socket, remote)) {
					socket.Close ();
					socket = null;
					continue;
				} else {
					try {
						operation.ThrowIfDisposed (cancellationToken);
						await socket.ConnectAsync (remote).ConfigureAwait (false);
					} catch (ObjectDisposedException) {
						throw;
					} catch (Exception exc) {
						Socket s = socket;
						socket = null;
						if (s != null)
							s.Close ();
						throw GetException (WebExceptionStatus.ConnectFailure, exc);
					}
				}

				if (socket != null)
					return socket;
			}

			throw GetException (WebExceptionStatus.ConnectFailure, null);
		}

		async Task<(bool, byte[])> CreateTunnel (WebConnectionData data, HttpWebRequest request, Uri connectUri,
							Stream stream, CancellationToken cancellationToken)
		{
			StringBuilder sb = new StringBuilder ();
			sb.Append ("CONNECT ");
			sb.Append (request.Address.Host);
			sb.Append (':');
			sb.Append (request.Address.Port);
			sb.Append (" HTTP/");
			if (request.ServicePoint.ProtocolVersion == HttpVersion.Version11)
				sb.Append ("1.1");
			else
				sb.Append ("1.0");

			sb.Append ("\r\nHost: ");
			sb.Append (request.Address.Authority);

			bool ntlm = false;
			var challenge = data.Challenge;
			data.Challenge = null;
			var auth_header = request.Headers["Proxy-Authorization"];
			bool have_auth = auth_header != null;
			if (have_auth) {
				sb.Append ("\r\nProxy-Authorization: ");
				sb.Append (auth_header);
				ntlm = auth_header.ToUpper ().Contains ("NTLM");
			} else if (challenge != null && data.StatusCode == 407) {
				ICredentials creds = request.Proxy.Credentials;
				have_auth = true;

				if (connect_request == null) {
					// create a CONNECT request to use with Authenticate
					connect_request = (HttpWebRequest)WebRequest.Create (
						connectUri.Scheme + "://" + connectUri.Host + ":" + connectUri.Port + "/");
					connect_request.Method = "CONNECT";
					connect_request.Credentials = creds;
				}

				if (creds != null) {
					for (int i = 0; i < challenge.Length; i++) {
						var auth = AuthenticationManager.Authenticate (challenge[i], connect_request, creds);
						if (auth == null)
							continue;
						ntlm = (auth.ModuleAuthenticationType == "NTLM");
						sb.Append ("\r\nProxy-Authorization: ");
						sb.Append (auth.Message);
						break;
					}
				}
			}

			if (ntlm) {
				sb.Append ("\r\nProxy-Connection: keep-alive");
				connect_ntlm_auth_state++;
			}

			sb.Append ("\r\n\r\n");

			data.StatusCode = 0;
			byte[] connectBytes = Encoding.Default.GetBytes (sb.ToString ());
			await stream.WriteAsync (connectBytes, 0, connectBytes.Length, cancellationToken).ConfigureAwait (false);

			var (result, buffer, status) = await ReadHeaders (data, stream, cancellationToken).ConfigureAwait (false);
			if ((!have_auth || connect_ntlm_auth_state == NtlmAuthState.Challenge) &&
			    result != null && status == 407) { // Needs proxy auth
				var connectionHeader = result["Connection"];
				if (data.Socket != null && !string.IsNullOrEmpty (connectionHeader) &&
				    connectionHeader.ToLower () == "close") {
					// The server is requesting that this connection be closed
					data.Socket.Close ();
					data.Socket = null;
				}

				data.StatusCode = status;
				data.Challenge = result.GetValues ("Proxy-Authenticate");
				data.Headers = result;
				return (false, buffer);
			}

			if (status != 200) {
				data.StatusCode = status;
				data.Headers = result;
				return (false, buffer);
			}

			return (result != null, buffer);
		}

		async Task<(WebHeaderCollection, byte[], int)> ReadHeaders (WebConnectionData data, Stream stream, CancellationToken cancellationToken)
		{
			byte[] retBuffer = null;
			int status = 200;

			byte[] buffer = new byte[1024];
			MemoryStream ms = new MemoryStream ();

			while (true) {
				cancellationToken.ThrowIfCancellationRequested ();
				int n = await stream.ReadAsync (buffer, 0, 1024, cancellationToken).ConfigureAwait (false);
				if (n == 0)
					throw GetException (WebExceptionStatus.ServerProtocolViolation, null);

				ms.Write (buffer, 0, n);
				int start = 0;
				string str = null;
				bool gotStatus = false;
				WebHeaderCollection headers = new WebHeaderCollection ();
				while (ReadLine (ms.GetBuffer (), ref start, (int)ms.Length, ref str)) {
					if (str == null) {
						int contentLen = 0;
						try {
							contentLen = int.Parse (headers["Content-Length"]);
						} catch {
							contentLen = 0;
						}

						if (ms.Length - start - contentLen > 0) {
							// we've read more data than the response header and conents,
							// give back extra data to the caller
							retBuffer = new byte[ms.Length - start - contentLen];
							Buffer.BlockCopy (ms.GetBuffer (), start + contentLen, retBuffer, 0, retBuffer.Length);
						} else {
							// haven't read in some or all of the contents for the response, do so now
							FlushContents (stream, contentLen - (int)(ms.Length - start));
						}

						return (headers, retBuffer, status);
					}

					if (gotStatus) {
						headers.Add (str);
						continue;
					}

					string[] parts = str.Split (' ');
					if (parts.Length < 2)
						throw GetException (WebExceptionStatus.ServerProtocolViolation, null);

					if (String.Compare (parts[0], "HTTP/1.1", true) == 0)
						data.ProxyVersion = HttpVersion.Version11;
					else if (String.Compare (parts[0], "HTTP/1.0", true) == 0)
						data.ProxyVersion = HttpVersion.Version10;
					else
						throw GetException (WebExceptionStatus.ServerProtocolViolation, null);

					status = (int)UInt32.Parse (parts[1]);
					if (parts.Length >= 3)
						data.StatusDescription = String.Join (" ", parts, 2, parts.Length - 2);

					gotStatus = true;
				}
			}
		}

		void FlushContents (Stream stream, int contentLength)
		{
			while (contentLength > 0) {
				byte[] contentBuffer = new byte[contentLength];
				int bytesRead = stream.Read (contentBuffer, 0, contentLength);
				if (bytesRead > 0) {
					contentLength -= bytesRead;
				} else {
					break;
				}
			}
		}

		static int nextID, nextRequestID;
		public readonly int ID = ++nextID;

		async Task<(WebExceptionStatus status, bool success, Exception error)> CreateStream (
			WebConnectionData data, bool reused, CancellationToken cancellationToken)
		{
			var requestID = ++nextRequestID;

			try {
				NetworkStream serverStream = new NetworkStream (data.Socket, false);

				Debug ($"WC CREATE STREAM: Cnc={ID} {requestID} {reused}");

				if (data.Request.Address.Scheme == Uri.UriSchemeHttps) {
#if SECURITY_DEP
					if (!reused || data.NetworkStream == null || data.MonoTlsStream == null) {
						byte[] buffer = null;
						if (sPoint.UseConnect) {
							bool ok;
							(ok, buffer) = await CreateTunnel (
								data, data.Request, sPoint.Address,
								serverStream, cancellationToken).ConfigureAwait (false);
							if (!ok)
								return (WebExceptionStatus.Success, false, null);
						}
						await data.Initialize (serverStream, buffer, cancellationToken).ConfigureAwait (false);
					}
					// we also need to set ServicePoint.Certificate 
					// and ServicePoint.ClientCertificate but this can
					// only be done later (after handshake - which is
					// done only after a read operation).
#else
					throw new NotSupportedException ();
#endif
				} else {
					data.Initialize (serverStream);
				}
			} catch (Exception ex) {
				ex = HttpWebRequest.FlattenException (ex);
				Debug ($"WC CREATE STREAM EX: Cnc={ID} {requestID} {data.Request.Aborted} - {status} - {ex.Message}");
				if (data.Request.Aborted || data.MonoTlsStream == null)
					return (WebExceptionStatus.ConnectFailure, false, ex);
				return (data.MonoTlsStream.ExceptionStatus, false, ex);
			} finally {
				Debug ($"WC CREATE STREAM DONE: Cnc={ID} {requestID}");
			}

			return (WebExceptionStatus.Success, true, null);
		}

		internal async Task<WebResponseStream> InitReadAsync (
			WebOperation operation, WebConnectionData data, CancellationToken cancellationToken)
		{
			Debug ($"WC INIT READ ASYNC: Cnc={ID} Op={operation.ID}");

			var buffer = new BufferOffsetSize (new byte[4096], false);
			var state = ReadState.None;
			int position = 0;

			while (true) {
				operation.ThrowIfClosedOrDisposed (cancellationToken);

				Debug ($"WC INIT READ ASYNC LOOP: Cnc={ID} Op={operation.ID} {state} - {buffer.Offset}/{buffer.Size}");

				var nread = await data.NetworkStream.ReadAsync (
					buffer.Buffer, buffer.Offset, buffer.Size, cancellationToken).ConfigureAwait (false);

				Debug ($"WC INIT READ ASYNC LOOP #1: Cnc={ID} Op={operation.ID} {state} - {buffer.Offset}/{buffer.Size} - {nread}");

				if (nread == 0)
					throw GetReadException (WebExceptionStatus.ReceiveFailure, null, "ReadDoneAsync2");

				if (nread < 0)
					throw GetReadException (WebExceptionStatus.ServerProtocolViolation, null, "ReadDoneAsync3");

				buffer.Offset += nread;
				buffer.Size -= nread;

				if (state == ReadState.None) {
					try {
						var oldPos = position;
						if (!GetResponse (data, sPoint, buffer, ref position, ref state))
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

			Debug ($"WC INIT READ ASYNC LOOP DONE: Cnc={ID} Op={operation.ID} - {buffer.Offset} {buffer.Size}");

			var stream = new WebResponseStream (this, operation, data);
			try {
				operation.ThrowIfDisposed (cancellationToken);
				await stream.Initialize (buffer, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				throw GetReadException (WebExceptionStatus.ReceiveFailure, e, "ReadDoneAsync6");
			}

			return stream;
		}

		static bool GetResponse (WebConnectionData data, ServicePoint sPoint, BufferOffsetSize buffer, ref int pos, ref ReadState state)
		{
			string line = null;
			bool lineok = false;
			bool isContinue = false;
			bool emptyFirstLine = false;
			do {
				if (state == ReadState.Aborted)
					throw GetReadException (WebExceptionStatus.RequestCanceled, null, "GetResponse");

				if (state == ReadState.None) {
					lineok = ReadLine (buffer.Buffer, ref pos, buffer.Offset, ref line);
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
						data.Version = HttpVersion.Version11;
						sPoint.SetVersion (HttpVersion.Version11);
					} else {
						data.Version = HttpVersion.Version10;
						sPoint.SetVersion (HttpVersion.Version10);
					}

					data.StatusCode = (int)UInt32.Parse (parts[1]);
					if (parts.Length >= 3)
						data.StatusDescription = String.Join (" ", parts, 2, parts.Length - 2);
					else
						data.StatusDescription = "";

					if (pos >= buffer.Size)
						return true;
				}

				emptyFirstLine = false;
				if (state == ReadState.Status) {
					state = ReadState.Headers;
					data.Headers = new WebHeaderCollection ();
					ArrayList headers = new ArrayList ();
					bool finished = false;
					while (!finished) {
						if (ReadLine (buffer.Buffer, ref pos, buffer.Offset, ref line) == false)
							break;

						if (line == null) {
							// Empty line: end of headers
							finished = true;
							continue;
						}

						if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t')) {
							int count = headers.Count - 1;
							if (count < 0)
								break;

							string prev = (string)headers[count] + line;
							headers[count] = prev;
						} else {
							headers.Add (line);
						}
					}

					if (!finished)
						return false;

					// .NET uses ParseHeaders or ParseHeadersStrict which is much better
					foreach (string s in headers) {

						int pos_s = s.IndexOf (':');
						if (pos_s == -1)
							throw new ArgumentException ("no colon found", "header");

						var header = s.Substring (0, pos_s);
						var value = s.Substring (pos_s + 1).Trim ();

						var h = data.Headers;
						if (WebHeaderCollection.AllowMultiValues (header)) {
							h.AddInternal (header, value);
						} else {
							h.SetInternal (header, value);
						}
					}

					if (data.StatusCode == (int)HttpStatusCode.Continue) {
						sPoint.SendContinue = true;
						if (pos >= buffer.Offset)
							return true;

						if (data.Request.ExpectContinue) {
							data.Request.DoContinueDelegate (data.StatusCode, data.Headers);
							// Prevent double calls when getting the
							// headers in several packets.
							data.Request.ExpectContinue = false;
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

		internal async Task<(WebConnectionData, WebRequestStream)> InitConnection (
			WebOperation operation, CancellationToken cancellationToken)
		{
			Debug ($"WC INIT CONNECTION: Cnc={ID} Req={operation.Request.ID} Op={operation.ID}");

			var request = operation.Request;
			request.WebConnection = this;
			if (request.ReuseConnection)
				request.StoredConnection = this;

			if (operation.Aborted)
				return (null, null);

			keepAlive = request.KeepAlive;
			var data = new WebConnectionData (this, operation);
			var oldData = Interlocked.Exchange (ref currentData, data);

		retry:
			bool reused = await CheckReusable (oldData, cancellationToken).ConfigureAwait (false);
			Debug ($"WC INIT CONNECTION #1: Cnc={ID} Op={operation.ID} data={data.ID} - {reused} - {operation.WriteBuffer != null} {operation.IsNtlmChallenge}");
			if (reused) {
				data.ReuseConnection (oldData);
			} else {
				try {
					data.Socket = await Connect (operation, data, cancellationToken).ConfigureAwait (false);
					Debug ($"WC INIT CONNECTION #2: Cnc={ID} Op={operation.ID} data={data.ID}");
				} catch (Exception ex) {
					Debug ($"WC INIT CONNECTION #2 FAILED: Cnc={ID} Op={operation.ID} data={data.ID} - {ex.Message}");
					if (!operation.Aborted)
						Close (true);
					throw;
				}
			}

			var streamResult = await CreateStream (data, reused, cancellationToken).ConfigureAwait (false);
			Debug ($"WC INIT CONNECTION #3: Cnc={ID} Op={operation.ID} data={data.ID} - {streamResult.status} {streamResult.success}");
			if (!streamResult.success) {
				if (operation.Aborted)
					return (null, null);

				if (streamResult.status == WebExceptionStatus.Success && data.Challenge != null)
					goto retry;

				if (streamResult.error == null && (data.StatusCode == 401 || data.StatusCode == 407)) {
					streamResult.status = WebExceptionStatus.ProtocolError;
					if (data.Headers == null)
						data.Headers = new WebHeaderCollection ();

					var webResponse = new HttpWebResponse (sPoint.Address, "CONNECT", data, null, null);
					streamResult.error = new WebException (
						data.StatusCode == 407 ? "(407) Proxy Authentication Required" : "(401) Unauthorized",
						null, streamResult.status, webResponse);
				}

				Close (true);
				throw GetException (streamResult.status, streamResult.error);
			}

			var stream = new WebRequestStream (this, operation, data);
			return (data, stream);
		}

		internal static WebException GetException (WebExceptionStatus status, Exception error)
		{
			if (error == null)
				return new WebException ($"Error: {status}", status);
			if (error is WebException wex)
				return wex;
			return new WebException ($"Error: {status} ({error.Message})", status,
						 WebExceptionInternalStatus.RequestFatal, error);
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

		static bool ReadLine (byte[] buffer, ref int start, int max, ref string output)
		{
			bool foundCR = false;
			StringBuilder text = new StringBuilder ();

			int c = 0;
			while (start < max) {
				c = (int)buffer[start++];

				if (c == '\n') {                        // newline
					if ((text.Length > 0) && (text[text.Length - 1] == '\r'))
						text.Length--;

					foundCR = false;
					break;
				} else if (foundCR) {
					text.Length--;
					break;
				}

				if (c == '\r')
					foundCR = true;


				text.Append ((char)c);
			}

			if (c != '\n' && c != '\r')
				return false;

			if (text.Length == 0) {
				output = null;
				return (c == '\n' || c == '\r');
			}

			if (foundCR)
				text.Length--;

			output = text.ToString ();
			return true;
		}

		async Task<bool> CompleteChunkedRead (WebConnectionData data, CancellationToken cancellationToken)
		{
			if (!data.ChunkedRead || data.ChunkStream == null)
				return true;

			var buffer = new byte[4096];
			while (data.ChunkStream.WantMore) {
				if (cancellationToken.IsCancellationRequested)
					return false;
				int nbytes = await data.NetworkStream.ReadAsync (buffer, 0, buffer.Length, cancellationToken).ConfigureAwait (false);
				if (nbytes <= 0)
					return false; // Socket was disconnected

				data.ChunkStream.Write (buffer, 0, nbytes);
			}

			return true;
		}

		internal void CloseError ()
		{
			Close (true);
		}

		internal void Reset ()
		{
			lock (this) {
				ResetNtlm ();
				currentData = new WebConnectionData ();

				connect_request = null;
				connect_ntlm_auth_state = NtlmAuthState.None;
			}
		}

		internal void ReallyCloseIt ()
		{
			Reset ();
		}

		internal void Close ()
		{
			Close (false);
		}

		[Obsolete ("KILL")]
		internal void Close (bool sendNext)
		{
			lock (this) {
				if (Data.Request != null && Data.Request.ReuseConnection) {
					Data.Request.ReuseConnection = false;
					return;
				}

				Data.Close ();

				if (ntlm_authenticated)
					ResetNtlm ();
				currentData = new WebConnectionData ();

				connect_request = null;
				connect_ntlm_auth_state = NtlmAuthState.None;
			}
		}

		internal void Abort (WebOperation operation)
		{
#if FIXME
			lock (this) {
				lock (queue) {
					HttpWebRequest req = operation.Request;
					if (Data.Request == req || Data.Request == null) {
						if (!req.FinishedReading) {
							status = WebExceptionStatus.RequestCanceled;
							Close ();
							if (queue.Count > 0) {
								operation = (WebOperation)queue.Dequeue ();
								SendRequest (operation);
							}
						}
						return;
					}

					req.FinishedReading = true;
					if (queue.Count > 0 && queue.Peek () == operation) {
						queue.Dequeue ();
					} else if (queue.Count > 0) {
						object[] old = queue.ToArray ();
						queue.Clear ();
						for (int i = old.Length - 1; i >= 0; i--) {
							if (old[i] != operation)
								queue.Enqueue (old[i]);
						}
					}
				}
			}
#endif
		}

		internal void ResetNtlm ()
		{
			ntlm_authenticated = false;
			ntlm_credentials = null;
			unsafe_sharing = false;
		}

		internal bool Connected {
			get {
				lock (this) {
					return (Data.Socket != null && Data.Socket.Connected);
				}
			}
		}

		// -Used for NTLM authentication
		internal WebOperation PriorityRequest {
			set { priority_request = value; }
		}

		internal bool NtlmAuthenticated {
			get { return ntlm_authenticated; }
			set { ntlm_authenticated = value; }
		}

		internal NetworkCredential NtlmCredential {
			get { return ntlm_credentials; }
			set { ntlm_credentials = value; }
		}

		internal bool UnsafeAuthenticatedConnectionSharing {
			get { return unsafe_sharing; }
			set { unsafe_sharing = value; }
		}
		// -
	}
}

