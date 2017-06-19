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
		byte[] buffer;
		EventHandler abortHandler;
		AbortHelper abortHelper;
		bool chunkedRead;
		MonoChunkStream chunkStream;
		Queue queue;
		bool reused;
		int position;
		HttpWebRequest priority_request;
		NetworkCredential ntlm_credentials;
		bool ntlm_authenticated;
		bool unsafe_sharing;

		internal WebConnectionData Data {
			get; private set;
		}

		enum NtlmAuthState
		{
			None,
			Challenge,
			Response
		}
		NtlmAuthState connect_ntlm_auth_state;
		HttpWebRequest connect_request;

		Exception connect_exception;

#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
		[System.Runtime.InteropServices.DllImport ("__Internal")]
		static extern void xamarin_start_wwan (string uri);
#endif

		internal MonoChunkStream MonoChunkStream {
			get { return chunkStream; }
		}

		public WebConnection (IWebConnectionState wcs, ServicePoint sPoint)
		{
			this.state = wcs;
			this.sPoint = sPoint;
			buffer = new byte[4096];
			Data = new WebConnectionData ();
			queue = wcs.Group.Queue;
			abortHelper = new AbortHelper ();
			abortHelper.Connection = this;
			abortHandler = new EventHandler (abortHelper.Abort);
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

		class AbortHelper
		{
			public WebConnection Connection;

			public void Abort (object sender, EventArgs args)
			{
				WebConnection other = ((HttpWebRequest)sender).WebConnection;
				if (other == null)
					other = Connection;
				other.Abort (sender, args);
			}
		}

		bool CanReuse (Socket socket)
		{
			// The real condition is !(socket.Poll (0, SelectMode.SelectRead) || socket.Available != 0)
			// but if there's data pending to read (!) we won't reuse the socket.
			return (socket.Poll (0, SelectMode.SelectRead) == false);
		}

		void Connect (HttpWebRequest request)
		{
			lock (socketLock) {
				WebConnectionData data = Data;
				if (data.Socket != null && data.Socket.Connected && status == WebExceptionStatus.Success) {
					// Take the chunked stream to the expected state (State.None)
					if (CanReuse (data.Socket) && CompleteChunkedRead (data)) {
						reused = true;
						return;
					}
				}

				reused = false;
				if (data.Socket != null) {
					data.Socket.Close ();
					data.Socket = null;
				}

				chunkStream = null;
				IPHostEntry hostEntry = sPoint.HostEntry;

				if (hostEntry == null) {
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					xamarin_start_wwan (sPoint.Address.ToString ());
					hostEntry = sPoint.HostEntry;
					if (hostEntry == null) {
#endif
					status = sPoint.UsesProxy ? WebExceptionStatus.ProxyNameResolutionFailure :
								    WebExceptionStatus.NameResolutionFailure;
					return;
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					}
#endif
				}

				//WebConnectionData data = Data;
				foreach (IPAddress address in hostEntry.AddressList) {
					Socket socket;
					try {
						socket = new Socket (address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
					} catch (Exception se) {
						// The Socket ctor can throw if we run out of FD's
						if (!request.Aborted)
							status = WebExceptionStatus.ConnectFailure;
						connect_exception = se;
						return;
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
						status = WebExceptionStatus.ConnectFailure;
					} else {
						try {
							if (request.Aborted)
								return;
							socket.Connect (remote);
						} catch (ThreadAbortException) {
							// program exiting...
							Socket s = socket;
							socket = null;
							if (s != null)
								s.Close ();
							return;
						} catch (ObjectDisposedException) {
							// socket closed from another thread
							return;
						} catch (Exception exc) {
							Socket s = socket;
							socket = null;
							if (s != null)
								s.Close ();
							if (!request.Aborted)
								status = WebExceptionStatus.ConnectFailure;
							connect_exception = exc;
						}
					}

					if (socket != null) {
						status = WebExceptionStatus.Success;
						data.Socket = socket;
						break;
					}
				}
			}
		}

		bool CreateTunnel (WebConnectionData data, HttpWebRequest request, Uri connectUri,
				   Stream stream, out byte[] buffer)
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
			stream.Write (connectBytes, 0, connectBytes.Length);

			int status;
			WebHeaderCollection result = ReadHeaders (stream, out buffer, out status);
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
				return false;
			}

			if (status != 200) {
				data.StatusCode = status;
				data.Headers = result;
				return false;
			}

			return (result != null);
		}

		WebHeaderCollection ReadHeaders (Stream stream, out byte[] retBuffer, out int status)
		{
			retBuffer = null;
			status = 200;

			byte[] buffer = new byte[1024];
			MemoryStream ms = new MemoryStream ();

			while (true) {
				int n = stream.Read (buffer, 0, 1024);
				if (n == 0) {
					HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders");
					return null;
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

						return headers;
					}

					if (gotStatus) {
						headers.Add (str);
						continue;
					}

					string[] parts = str.Split (' ');
					if (parts.Length < 2) {
						HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders2");
						return null;
					}

					if (String.Compare (parts[0], "HTTP/1.1", true) == 0)
						Data.ProxyVersion = HttpVersion.Version11;
					else if (String.Compare (parts[0], "HTTP/1.0", true) == 0)
						Data.ProxyVersion = HttpVersion.Version10;
					else {
						HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadHeaders2");
						return null;
					}

					status = (int)UInt32.Parse (parts[1]);
					if (parts.Length >= 3)
						Data.StatusDescription = String.Join (" ", parts, 2, parts.Length - 2);

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

		bool CreateStream (HttpWebRequest request)
		{
			var requestID = ++nextRequestID;
			var data = Data;

			try {
				NetworkStream serverStream = new NetworkStream (data.Socket, false);

				Debug ($"WC CREATE STREAM: {ID} {requestID}");

				if (request.Address.Scheme == Uri.UriSchemeHttps) {
#if SECURITY_DEP
					if (!reused || data.NetworkStream == null || data.MonoTlsStream == null) {
						byte [] buffer = null;
						if (sPoint.UseConnect) {
							bool ok = CreateTunnel (data, request, sPoint.Address, serverStream, out buffer);
							if (!ok)
								return false;
						}
						data.Initialize (request, serverStream, buffer);
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
				if (request.Aborted || data.MonoTlsStream == null)
					status = WebExceptionStatus.ConnectFailure;
				else {
					status = data.MonoTlsStream.ExceptionStatus;
					Debug ($"WC CREATE STREAM EX: {ID} {requestID} - {status} - {ex.Message}");
				}
				connect_exception = ex;
				return false;
			} finally {
				Debug ($"WC CREATE STREAM DONE: {ID} {requestID}");
			}

			return true;
		}

		void HandleError (WebExceptionStatus st, Exception e, string where)
		{
			status = st;
			lock (this) {
				if (st == WebExceptionStatus.RequestCanceled)
					Data = new WebConnectionData ();
			}

			if (e == null) { // At least we now where it comes from
				try {
					throw new Exception (new System.Diagnostics.StackTrace ().ToString ());
				} catch (Exception e2) {
					e = e2;
				}
			}

			HttpWebRequest req = null;
			if (Data != null && Data.request != null)
				req = Data.request;

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

		internal void InitReadAsync (CancellationToken cancellationToken)
		{
			WebConnectionData data = Data;

			try {
				int size = buffer.Length - position;
				int requestId = ++nextRequestID;
				Debug ($"WC INIT READ ASYNC: {ID} {data.ID} {requestId}");
				cancellationToken.ThrowIfCancellationRequested ();
				var task = data.NetworkStream.ReadAsync (buffer, position, size, cancellationToken);
				task.ContinueWith (t => ReadDoneAsync (data, t, cancellationToken));
				Debug ($"WC INIT READ ASYNC DONE: {ID} {data.ID} {requestId} - {task}");
			} catch (ObjectDisposedException) {
				return;
			} catch (OperationCanceledException) {
				return;
			} catch (Exception e) {
				e = HttpWebRequest.FlattenException (e);
				if (e.InnerException is ObjectDisposedException)
					return;

				HandleError (WebExceptionStatus.ReceiveFailure, e, "ReadDoneAsync1");
				return;
			}

		}

		async void ReadDoneAsync (WebConnectionData data, Task<int> task, CancellationToken cancellationToken)
		{
			if (task.IsCanceled)
				return;
			if (task.IsFaulted) {
				var e = HttpWebRequest.FlattenException (task.Exception);
				if (e is ObjectDisposedException || e.InnerException is ObjectDisposedException)
					return;
				HandleError (WebExceptionStatus.ReceiveFailure, e, "ReadDoneAsync1");
				return;
			}

			int nread = task.Result;
			if (nread == 0) {
				HandleError (WebExceptionStatus.ReceiveFailure, null, "ReadDoneAsync2");
				return;
			}

			if (nread < 0) {
				HandleError (WebExceptionStatus.ServerProtocolViolation, null, "ReadDoneAsync3");
				return;
			}

			int pos = -1;
			nread += position;
			if (data.ReadState == ReadState.None) {
				Exception exc = null;
				try {
					pos = GetResponse (data, sPoint, buffer, nread);
				} catch (Exception e) {
					exc = e;
				}

				if (exc != null || pos == -1) {
					HandleError (WebExceptionStatus.ServerProtocolViolation, exc, "ReadDoneAsync4");
					return;
				}
			}

			if (data.ReadState == ReadState.Aborted) {
				HandleError (WebExceptionStatus.RequestCanceled, null, "ReadDoneAsync5");
				return;
			}

			if (data.ReadState != ReadState.Content) {
				int est = nread * 2;
				int max = (est < buffer.Length) ? buffer.Length : est;
				byte[] newBuffer = new byte[max];
				Buffer.BlockCopy (buffer, 0, newBuffer, 0, nread);
				buffer = newBuffer;
				position = nread;
				data.ReadState = ReadState.None;
				InitReadAsync (cancellationToken);
				return;
			}

			position = 0;

			WebConnectionStream stream = await WebConnectionStream.Create (this, data, cancellationToken).ConfigureAwait (false);
			bool expect_content = ExpectContent (data.StatusCode, data.request.Method);
			string tencoding = null;
			if (expect_content)
				tencoding = data.Headers["Transfer-Encoding"];

			chunkedRead = (tencoding != null && tencoding.IndexOf ("chunked", StringComparison.OrdinalIgnoreCase) != -1);
			if (!chunkedRead) {
				stream.ReadBuffer = buffer;
				stream.ReadBufferOffset = pos;
				stream.ReadBufferSize = nread;
				try {
					await stream.CheckResponseInBuffer (cancellationToken).ConfigureAwait (false);
				} catch (Exception e) {
					HandleError (WebExceptionStatus.ReceiveFailure, e, "ReadDoneAsync6");
				}
			} else if (chunkStream == null) {
				try {
					chunkStream = new MonoChunkStream (buffer, pos, nread, data.Headers);
				} catch (Exception e) {
					HandleError (WebExceptionStatus.ServerProtocolViolation, e, "ReadDoneAsync7");
					return;
				}
			} else {
				chunkStream.ResetBuffer ();
				try {
					chunkStream.Write (buffer, pos, nread);
				} catch (Exception e) {
					HandleError (WebExceptionStatus.ServerProtocolViolation, e, "ReadDoneAsync8");
					return;
				}
			}

			data.stream = stream;

			if (!expect_content) {
				if (stream.ForceCompletion ())
					NextRead ();
			}

			try {
				data.request.SetResponseData (data);
			} catch (Exception e) {
				Console.Error.WriteLine ("READ DONE EX: {0}", e);
			}
		}

		static int GetResponse (WebConnectionData data, ServicePoint sPoint,
					byte[] buffer, int max)
		{
			int pos = 0;
			string line = null;
			bool lineok = false;
			bool isContinue = false;
			bool emptyFirstLine = false;
			do {
				if (data.ReadState == ReadState.Aborted)
					return -1;

				if (data.ReadState == ReadState.None) {
					lineok = ReadLine (buffer, ref pos, max, ref line);
					if (!lineok)
						return 0;

					if (line == null) {
						emptyFirstLine = true;
						continue;
					}
					emptyFirstLine = false;
					data.ReadState = ReadState.Status;

					string[] parts = line.Split (' ');
					if (parts.Length < 2)
						return -1;

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

					if (pos >= max)
						return pos;
				}

				emptyFirstLine = false;
				if (data.ReadState == ReadState.Status) {
					data.ReadState = ReadState.Headers;
					data.Headers = new WebHeaderCollection ();
					ArrayList headers = new ArrayList ();
					bool finished = false;
					while (!finished) {
						if (ReadLine (buffer, ref pos, max, ref line) == false)
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
						return 0;

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
						if (pos >= max)
							return pos;

						if (data.request.ExpectContinue) {
							data.request.DoContinueDelegate (data.StatusCode, data.Headers);
							// Prevent double calls when getting the
							// headers in several packets.
							data.request.ExpectContinue = false;
						}

						data.ReadState = ReadState.None;
						isContinue = true;
					} else {
						data.ReadState = ReadState.Content;
						return pos;
					}
				}
			} while (emptyFirstLine || isContinue);

			return -1;
		}

		void InitConnection (HttpWebRequest request, int debugID)
		{
			Debug ($"WC INIT CONNECTION: {ID} {request.ID} {debugID}"); 

			request.WebConnection = this;
			if (request.ReuseConnection)
				request.StoredConnection = this;

			if (request.Aborted)
				return;

			keepAlive = request.KeepAlive;
			Data = new WebConnectionData (request);
		retry:
			Connect (request);
			if (request.Aborted)
				return;

			if (status != WebExceptionStatus.Success) {
				if (!request.Aborted) {
					request.SetWriteStreamError (status, connect_exception);
					Close (true);
				}
				return;
			}

			if (!CreateStream (request)) {
				if (request.Aborted)
					return;

				WebExceptionStatus st = status;
				if (Data.Challenge != null)
					goto retry;

				Exception cnc_exc = connect_exception;
				if (cnc_exc == null && (Data.StatusCode == 401 || Data.StatusCode == 407)) {
					st = WebExceptionStatus.ProtocolError;
					if (Data.Headers == null)
						Data.Headers = new WebHeaderCollection ();

					var webResponse = new HttpWebResponse (sPoint.Address, "CONNECT", Data, null);
					cnc_exc = new WebException (Data.StatusCode == 407 ? "(407) Proxy Authentication Required" : "(401) Unauthorized", null, st, webResponse);
				}

				connect_exception = null;
				request.SetWriteStreamError (st, cnc_exc);
				Close (true);
				return;
			}

			request.SetWriteStream (new WebConnectionStream (this, request));
		}

#if MONOTOUCH
		static bool warned_about_queue = false;
#endif

		static int nextSendID;

		internal EventHandler SendRequest (HttpWebRequest request)
		{
			if (request.Aborted)
				return null;

			lock (this) {
				var requestID = ++nextSendID;
				Debug ($"WC SEND REQUEST: {ID} {request.ID} {requestID}");
				if (state.TrySetBusy ()) {
					status = WebExceptionStatus.Success;
					ThreadPool.QueueUserWorkItem (_ => {
						try {
							Debug ($"WC SEND REQUEST #1: {ID} {request.ID} {requestID}");
							InitConnection (request, requestID);
						} catch (Exception ex) {
							Debug ($"WC SEND REQUEST EX: {ID} {request.ID} {requestID} - {ex}");
							throw;
						}
					}, null);
				} else {
					lock (queue) {
#if MONOTOUCH
						if (!warned_about_queue) {
							warned_about_queue = true;
							Console.WriteLine ("WARNING: An HttpWebRequest was added to the ConnectionGroup queue because the connection limit was reached.");
						}
#endif
						queue.Enqueue (request);
						Debug ($"WC SEND REQUEST - QUEUED: {ID} {request.ID} {requestID}");
					}
				}
			}

			return abortHandler;
		}

		void SendNext ()
		{
			lock (queue) {
				Debug ($"WC SEND NEXT: {ID} {queue.Count}");
				if (queue.Count > 0) {
					SendRequest ((HttpWebRequest)queue.Dequeue ());
				}
			}
		}

		internal void NextRead ()
		{
			lock (this) {
				Debug ($"WC NEXT READ: {ID}");
				if (Data.request != null)
					Data.request.FinishedReading = true;
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
				if (priority_request != null) {
					SendRequest (priority_request);
					priority_request = null;
				} else {
					SendNext ();
				}
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

		internal WebConnectionAsyncResult StartAsyncOperation (HttpWebRequest request, AsyncCallback callback, object state)
		{
			WebConnectionData data;
			Stream s;
			lock (this) {
				if (Data.request != request)
					throw new ObjectDisposedException (typeof (NetworkStream).FullName);
				data = Data;
				s = data.NetworkStream;
				if (s == null)
					throw new ObjectDisposedException (typeof (NetworkStream).FullName);
			}

			return new WebConnectionAsyncResult (callback, state, data, request, s);
		}

		internal async Task<int> ReadAsync (HttpWebRequest request, byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			Debug ($"WC READ ASYNC: {ID}");

			cancellationToken.ThrowIfCancellationRequested ();

			WebConnectionData data;
			Stream s;
			lock (this) {
				if (Data.request != request)
					throw new ObjectDisposedException (typeof (NetworkStream).FullName);
				data = Data;
				s = data.NetworkStream;
				if (s == null)
					throw new ObjectDisposedException (typeof (NetworkStream).FullName);
			}

			int nbytes = 0;
			bool done = false;

			if (!chunkedRead || (!chunkStream.DataAvailable && chunkStream.WantMore)) {
				nbytes = await s.ReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
				Debug ($"WC READ ASYNC #1: {ID} {nbytes} {chunkedRead}");
				if (!chunkedRead)
					return nbytes;
				done = nbytes == 0;
			}

			try {
				chunkStream.WriteAndReadBack (buffer, offset, size, ref nbytes);
				Debug ($"WC READ ASYNC #1: {ID} {done} {nbytes} {chunkStream.WantMore}");
				if (!done && nbytes == 0 && chunkStream.WantMore)
					nbytes = await EnsureReadAsync (data, buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				if (e is WebException || e is OperationCanceledException)
					throw;
				throw new WebException ("Invalid chunked data.", e, WebExceptionStatus.ServerProtocolViolation, null);
			}

			if ((done || nbytes == 0) && chunkStream.ChunkLeft != 0) {
				HandleError (WebExceptionStatus.ReceiveFailure, null, "chunked EndRead");
				throw new WebException ("Read error", null, WebExceptionStatus.ReceiveFailure, null);
			}

			return nbytes;
		}

		async Task<int> EnsureReadAsync (WebConnectionData data, byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			byte[] morebytes = null;
			int nbytes = 0;
			while (nbytes == 0 && chunkStream.WantMore && !cancellationToken.IsCancellationRequested) {
				int localsize = chunkStream.ChunkLeft;
				if (localsize <= 0) // not read chunk size yet
					localsize = 1024;
				else if (localsize > 16384)
					localsize = 16384;

				if (morebytes == null || morebytes.Length < localsize)
					morebytes = new byte[localsize];

				int nread = await data.NetworkStream.ReadAsync (morebytes, 0, localsize, cancellationToken).ConfigureAwait (false);
				if (nread <= 0)
					return 0; // Error

				chunkStream.Write (morebytes, 0, nread);
				nbytes += chunkStream.Read (buffer, offset + nbytes, size - nbytes);
			}

			return nbytes;
		}

		bool CompleteChunkedRead (WebConnectionData data)
		{
			if (!chunkedRead || chunkStream == null)
				return true;

			while (chunkStream.WantMore) {
				int nbytes = data.NetworkStream.Read (buffer, 0, buffer.Length);
				if (nbytes <= 0)
					return false; // Socket was disconnected

				chunkStream.Write (buffer, 0, nbytes);
			}

			return true;
  		}

		internal async Task WriteAsync (HttpWebRequest request, byte[] buffer, int offset, int size,
		                                CancellationToken cancellationToken)
		{
			Debug ($"WC WRITE ASYNC: {ID}");

			cancellationToken.ThrowIfCancellationRequested ();

			WebConnectionData data;
			Stream s;
			lock (this) {
				if (status == WebExceptionStatus.RequestCanceled)
					return;
				if (Data.request != request)
					throw new ObjectDisposedException (typeof (NetworkStream).FullName);
				data = Data;
				s = data.NetworkStream;
				if (s == null)
					throw new ObjectDisposedException (typeof (NetworkStream).FullName);
			}

			try {
				await s.WriteAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (ObjectDisposedException) {
				lock (this) {
					if (Data != data || data.request != request)
						return;
				}
				throw;
			} catch (IOException e) {
				// FIXME: do we need this?
				SocketException se = e.InnerException as SocketException;
				if (se != null && se.SocketErrorCode == SocketError.NotConnected) {
					return;
				}
				throw;
			} catch {
				status = WebExceptionStatus.SendFailure;
				throw;
			} finally {
				Debug ($"WC WRITE ASYNC DONE: {ID}");
			}
		}

		internal void CloseError ()
		{
			Close (true);
		}

		internal void Close (bool sendNext)
		{
			lock (this) {
				if (Data.request != null && Data.request.ReuseConnection) {
					Data.request.ReuseConnection = false;
					return;
				}

				Data.Close ();

				if (ntlm_authenticated)
					ResetNtlm ();
				state.SetIdle ();
				Data = new WebConnectionData ();
				if (sendNext)
					SendNext ();
				
				connect_request = null;
				connect_ntlm_auth_state = NtlmAuthState.None;
			}
		}

		void Abort (object sender, EventArgs args)
		{
			lock (this) {
				lock (queue) {
					HttpWebRequest req = (HttpWebRequest) sender;
					if (Data.request == req || Data.request == null) {
						if (!req.FinishedReading) {
							status = WebExceptionStatus.RequestCanceled;
							Close (false);
							if (queue.Count > 0) {
								Data.request = (HttpWebRequest) queue.Dequeue ();
								SendRequest (Data.request);
							}
						}
						return;
					}

					req.FinishedReading = true;
					req.SetResponseError (WebExceptionStatus.RequestCanceled, null, "User aborted");
					if (queue.Count > 0 && queue.Peek () == sender) {
						queue.Dequeue ();
					} else if (queue.Count > 0) {
						object [] old = queue.ToArray ();
						queue.Clear ();
						for (int i = old.Length - 1; i >= 0; i--) {
							if (old [i] != sender)
								queue.Enqueue (old [i]);
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
		internal HttpWebRequest PriorityRequest {
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

