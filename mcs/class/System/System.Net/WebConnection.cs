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
		IWebConnectionState state;
		WebExceptionStatus status;
		bool keepAlive;
		Queue queue;
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

		public WebConnection (IWebConnectionState wcs, ServicePoint sPoint)
		{
			this.state = wcs;
			this.sPoint = sPoint;
			// buffer = new byte[4096];
			currentData = new WebConnectionData ();
			queue = wcs.Group.Queue;
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

		async Task<(WebExceptionStatus status, Socket socket, Exception error)> Connect (
			WebOperation operation, WebConnectionData data, CancellationToken cancellationToken)
		{
			IPHostEntry hostEntry = sPoint.HostEntry;

			if (hostEntry == null || hostEntry.AddressList.Length == 0) {
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					xamarin_start_wwan (sPoint.Address.ToString ());
					hostEntry = sPoint.HostEntry;
					if (hostEntry == null) {
#endif
				return (sPoint.UsesProxy ? WebExceptionStatus.ProxyNameResolutionFailure :
					WebExceptionStatus.NameResolutionFailure, null, null);
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					}
#endif
			}

			foreach (IPAddress address in hostEntry.AddressList) {
				if (operation.Aborted)
					return (WebExceptionStatus.RequestCanceled, null, null);

				Socket socket;
				try {
					socket = new Socket (address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
				} catch (Exception se) {
					// The Socket ctor can throw if we run out of FD's
					return (WebExceptionStatus.ConnectFailure, null, se);
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
						if (operation.Aborted)
							return (WebExceptionStatus.RequestCanceled, null, null);
						await socket.ConnectAsync (remote).ConfigureAwait (false);
					} catch (ThreadAbortException) {
						// program exiting...
						Socket s = socket;
						socket = null;
						if (s != null)
							s.Close ();
						return (WebExceptionStatus.RequestCanceled, null, null);
					} catch (ObjectDisposedException) {
						// socket closed from another thread
						return (WebExceptionStatus.RequestCanceled, null, null);
					} catch (Exception exc) {
						Socket s = socket;
						socket = null;
						if (s != null)
							s.Close ();
						return (WebExceptionStatus.ConnectFailure, null, exc);
					}
				}

				if (socket != null)
					return (WebExceptionStatus.Success, socket, null);
			}

			return (WebExceptionStatus.ConnectFailure, null, null);
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
				if (n == 0) {
					HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders");
					return (null, null, 200);
				}

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
					if (parts.Length < 2) {
						HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders2");
						return (null, null, 200);
					}

					if (String.Compare (parts[0], "HTTP/1.1", true) == 0)
						data.ProxyVersion = HttpVersion.Version11;
					else if (String.Compare (parts[0], "HTTP/1.0", true) == 0)
						data.ProxyVersion = HttpVersion.Version10;
					else {
						HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders2");
						return (null, null, 200);
					}

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

				Debug ($"WC CREATE STREAM: {ID} {requestID} {reused}");

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
				Debug ($"WC CREATE STREAM EX: {ID} {requestID} {data.Request.Aborted} - {status} - {ex.Message}");
				if (data.Request.Aborted || data.MonoTlsStream == null)
					return (WebExceptionStatus.ConnectFailure, false, ex);
				return (data.MonoTlsStream.ExceptionStatus, false, ex);
			} finally {
				Debug ($"WC CREATE STREAM DONE: {ID} {requestID}");
			}

			return (WebExceptionStatus.Success, true, null);
		}

		void HandleError (WebExceptionStatus st, Exception e, string where)
		{
			status = st;
			lock (this) {
				Debug ($"WC HANDLE ERROR: {ID} {st} {where} - {e}");

				if (st == WebExceptionStatus.RequestCanceled)
					currentData = new WebConnectionData ();
			}

			if (e == null) { // At least we now where it comes from
				try {
					throw new Exception (new System.Diagnostics.StackTrace ().ToString ());
				} catch (Exception e2) {
					e = e2;
				}
			}

			HttpWebRequest req = null;
			if (Data != null && Data.Request != null)
				req = Data.Request;

			Close (true);
			if (req != null) {
				req.FinishedReading = true;
				req.SetResponseError (st, e, where);
			}
		}

		static bool ExpectContent (int statusCode, string method)
		{
			if (method == "HEAD")
				return false;
			return (statusCode >= 200 && statusCode != 204 && statusCode != 304);
		}

		async Task<(WebResponseStream, Exception)> NewInitReadAsync (
			WebOperation operation, WebConnectionData data, CancellationToken cancellationToken)
		{
			Debug ($"WC INIT READ ASYNC: {ID} {operation.ID}");

			var buffer = new BufferOffsetSize (new byte[4096], false);
			var state = ReadState.None;
			int position = 0;

			while (true) {
				operation.ThrowIfClosedOrDisposed (cancellationToken);

				Debug ($"WC INIT READ ASYNC LOOP: {ID} {operation.ID} {state} - {buffer.Offset}/{buffer.Size}");

				var nread = await data.NetworkStream.ReadAsync (
					buffer.Buffer, buffer.Offset, buffer.Size, cancellationToken).ConfigureAwait (false);

				Debug ($"WC INIT READ ASYNC LOOP #1: {ID} {operation.ID} {state} - {buffer.Offset}/{buffer.Size} - {nread}");

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

			Debug ($"WC INIT READ ASYNC LOOP DONE: {ID} {operation.ID} - {buffer.Offset} {buffer.Size}");

			var stream = new WebResponseStream (this, operation, data);
			bool expect_content = ExpectContent (data.StatusCode, data.Request.Method);
			string tencoding = null;
			if (expect_content)
				tencoding = data.Headers["Transfer-Encoding"];

			data.ChunkedRead = (tencoding != null && tencoding.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			if (!data.ChunkedRead) {
				stream.ReadBuffer = buffer.Buffer;
				stream.ReadBufferOffset = buffer.Offset;
				stream.ReadBufferSize = buffer.Size;
				try {
					await stream.CheckResponseInBuffer (cancellationToken).ConfigureAwait (false);
				} catch (Exception e) {
					throw GetReadException (WebExceptionStatus.ReceiveFailure, e, "ReadDoneAsync6");
				}
			} else if (data.ChunkStream == null) {
				try {
					data.ChunkStream = new MonoChunkStream (buffer.Buffer, buffer.Offset, buffer.Size, data.Headers);
				} catch (Exception e) {
					throw GetReadException (WebExceptionStatus.ServerProtocolViolation, e, "ReadDoneAsync7");
				}
			} else {
				data.ChunkStream.ResetBuffer ();
				try {
					data.ChunkStream.Write (buffer.Buffer, buffer.Offset, buffer.Size);
				} catch (Exception e) {
					throw GetReadException (WebExceptionStatus.ServerProtocolViolation, e, "ReadDoneAsync8");
				}
			}

			data.stream = stream;

			if (!expect_content) {
				if (stream.ForceCompletion ())
					NextRead ();
			}

			try {
				data.Request.SetResponseData (data);
			} catch (Exception e) {
				Console.Error.WriteLine ("READ DONE EX: {0}", e);
			}


			throw new NotImplementedException ();
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

		async Task<(WebConnectionData, WebRequestStream, Exception)> InitConnection (
			WebOperation operation, CancellationToken cancellationToken)
		{
			Debug ($"WC INIT CONNECTION: {ID} {operation.Request.ID} {operation.ID}");

			var request = operation.Request;
			request.WebConnection = this;
			if (request.ReuseConnection)
				request.StoredConnection = this;

			if (operation.Aborted)
				return (null, null, null);

			keepAlive = request.KeepAlive;
			var data = new WebConnectionData (this, operation);
			var oldData = Interlocked.Exchange (ref currentData, data);

		retry:
			bool reused = await CheckReusable (oldData, cancellationToken).ConfigureAwait (false);
			Debug ($"WC INIT CONNECTION #1: {ID} {operation.ID} {data.ID} - {reused} - {operation.WriteBuffer != null} {operation.IsNtlmChallenge}");
			if (reused) {
				data.ReuseConnection (oldData);
			} else {
				var connectResult = await Connect (operation, data, cancellationToken).ConfigureAwait (false);
				Debug ($"WC INIT CONNECTION #2: {ID} {operation.ID} {data.ID} - {connectResult.status}");
				if (operation.Aborted)
					return (null, null, null);

				if (connectResult.status != WebExceptionStatus.Success) {
					Close (true);
					return (data, null, GetException (connectResult.status, connectResult.error));
				}

				data.Socket = connectResult.socket;
			}

			var streamResult = await CreateStream (data, reused, cancellationToken).ConfigureAwait (false);
			Debug ($"WC INIT CONNECTION #3: {ID} {operation.ID} {data.ID} - {streamResult.status} {streamResult.success}");
			if (!streamResult.success) {
				if (operation.Aborted)
					return (null, null, null);

				if (streamResult.status == WebExceptionStatus.Success && data.Challenge != null)
					goto retry;

				if (streamResult.error == null && (data.StatusCode == 401 || data.StatusCode == 407)) {
					streamResult.status = WebExceptionStatus.ProtocolError;
					if (data.Headers == null)
						data.Headers = new WebHeaderCollection ();

					var webResponse = new HttpWebResponse (sPoint.Address, "CONNECT", data, null);
					streamResult.error = new WebException (
						data.StatusCode == 407 ? "(407) Proxy Authentication Required" : "(401) Unauthorized",
						null, streamResult.status, webResponse);
				}

				Close (true);
				return (data, null, GetException (streamResult.status, streamResult.error));
			}

			var stream = new WebRequestStream (this, operation, data);

			operation.Run (token => NewInitReadAsync (operation, data, token));
			// InitReadAsync (operation, data, cancellationToken);

			try {
				await stream.Initialize (cancellationToken);
			} catch (Exception ex) {
				return (data, stream, GetException (WebExceptionStatus.SendFailure, ex));
			}

			return (data, stream, null);
		}

		static WebException GetException (WebExceptionStatus status, Exception error)
		{
			if (error == null)
				return new WebException ($"Error: {status}", status);
			if (error is WebException wex)
				return wex;
			return new WebException ($"Error: {status} ({error.Message})", status,
						 WebExceptionInternalStatus.RequestFatal, error);
		}

		static WebException GetReadException (WebExceptionStatus status, Exception error, string where)
		{
			string msg = $"Error getting response stream ({where}): {status}";
			if (error == null)
				return new WebException ($"Error getting response stream ({where}): {status}", status);
			if (error is WebException wex)
				return wex;
			return new WebException ($"Error getting response stream ({where}): {status} {error.Message}", status,
						 WebExceptionInternalStatus.RequestFatal, error);
		}

#if MONOTOUCH
		static bool warned_about_queue = false;
#endif

		internal void SendRequest (WebOperation operation)
		{
			lock (this) {
				if (operation.Aborted)
					return;
				Debug ($"WC SEND REQUEST: {ID} {operation.ID}");
				if (state.TrySetBusy ()) {
					status = WebExceptionStatus.Success;
					try {
						Debug ($"WC SEND REQUEST #1: {ID} {operation.ID}");
						operation.Run (token => InitConnection (operation, token));
					} catch (Exception ex) {
						Debug ($"WC SEND REQUEST EX: {ID} {operation.ID} - {ex}");
						throw;
					}
				} else {
					lock (queue) {
#if MONOTOUCH
						if (!warned_about_queue) {
							warned_about_queue = true;
							Console.WriteLine ("WARNING: An HttpWebRequest was added to the ConnectionGroup queue because the connection limit was reached.");
						}
#endif
						queue.Enqueue (operation);
						Debug ($"WC SEND REQUEST - QUEUED: {ID} {operation.ID}");
					}
				}
			}
		}

		void SendNext ()
		{
			lock (queue) {
				Debug ($"WC SEND NEXT: {ID} {queue.Count}");
				if (queue.Count > 0) {
					SendRequest ((WebOperation)queue.Dequeue ());
				}
			}
		}

		internal void NextRead ()
		{
			lock (this) {
				Debug ($"WC NEXT READ: {ID}");
				if (Data.Request != null)
					Data.Request.FinishedReading = true;
				string header = (sPoint.UsesProxy) ? "Proxy-Connection" : "Connection";
				string cncHeader = (Data.Headers != null) ? Data.Headers[header] : null;
				bool keepAlive = (Data.Version == HttpVersion.Version11 && this.keepAlive);
				if (Data.ProxyVersion != null && Data.ProxyVersion != HttpVersion.Version11)
					keepAlive = false;
				if (cncHeader != null) {
					cncHeader = cncHeader.ToLower ();
					keepAlive = (this.keepAlive && cncHeader.IndexOf ("keep-alive", StringComparison.Ordinal) != -1);
				}

				if ((Data.Socket != null && !Data.Socket.Connected) ||
				   (!keepAlive || (cncHeader != null && cncHeader.IndexOf ("close", StringComparison.Ordinal) != -1))) {
					Close (false);
				}

				state.SetIdle ();
				var operation = Interlocked.Exchange (ref priority_request, null);
				if (operation != null && !operation.Aborted)
					SendRequest (operation);
				else
					SendNext ();
			}
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
				state.SetIdle ();
				currentData = new WebConnectionData ();
				if (sendNext)
					SendNext ();

				connect_request = null;
				connect_ntlm_auth_state = NtlmAuthState.None;
			}
		}

		internal void Abort (WebOperation operation)
		{
			lock (this) {
				lock (queue) {
					HttpWebRequest req = operation.Request;
					if (Data.Request == req || Data.Request == null) {
						if (!req.FinishedReading) {
							status = WebExceptionStatus.RequestCanceled;
							Close (false);
							if (queue.Count > 0) {
								operation = (WebOperation)queue.Dequeue ();
								SendRequest (operation);
							}
						}
						return;
					}

					req.FinishedReading = true;
					req.SetResponseError (WebExceptionStatus.RequestCanceled, null, "User aborted");
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

