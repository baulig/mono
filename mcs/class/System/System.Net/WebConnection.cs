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
		bool keepAlive;
		NetworkCredential ntlm_credentials;
		bool ntlm_authenticated;
		bool unsafe_sharing;

		WebConnectionData currentData;
		WebConnectionData Data {
			get { return currentData; }
		}

#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
		[System.Runtime.InteropServices.DllImport ("__Internal")]
		static extern void xamarin_start_wwan (string uri);
#endif

		public WebConnection (ServicePoint sPoint)
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

			if (data.Socket != null && data.Socket.Connected) {
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

		static int nextID, nextRequestID;
		public readonly int ID = ++nextID;

		async Task<(WebExceptionStatus status, bool success, WebConnectionTunnel tunnel, Exception error)> CreateStream (
			WebConnectionData data, bool reused, WebConnectionTunnel tunnel, CancellationToken cancellationToken)
		{
			var requestID = ++nextRequestID;

			try {
				NetworkStream serverStream = new NetworkStream (data.Socket, false);

				Debug ($"WC CREATE STREAM: Cnc={ID} data={data.ID} {requestID} {reused} socket={data.Socket.ID}");

				if (data.Request.Address.Scheme == Uri.UriSchemeHttps) {
					if (!reused || data.NetworkStream == null || data.MonoTlsStream == null) {
						if (sPoint.UseConnect) {
							if (tunnel == null)
								tunnel = new WebConnectionTunnel (data.Request, sPoint.Address);
							await tunnel.Initialize (serverStream, cancellationToken).ConfigureAwait (false);
							if (!tunnel.Success)
								return (WebExceptionStatus.Success, false, tunnel, null);
						}
						await data.Initialize (serverStream, tunnel, cancellationToken).ConfigureAwait (false);
					}
					return (WebExceptionStatus.Success, true, tunnel, null);
				}

				data.Initialize (serverStream);
				return (WebExceptionStatus.Success, true, null, null);
			} catch (Exception ex) {
				ex = HttpWebRequest.FlattenException (ex);
				Debug ($"WC CREATE STREAM EX: Cnc={ID} {requestID} {data.Request.Aborted} - {ex.Message}");
				if (data.Request.Aborted || data.MonoTlsStream == null)
					return (WebExceptionStatus.ConnectFailure, false, null, ex);
				return (data.MonoTlsStream.ExceptionStatus, false, null, ex);
			} finally {
				Debug ($"WC CREATE STREAM DONE: Cnc={ID} {requestID}");
			}
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
			WebConnectionTunnel tunnel = null;

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
					throw;
				}
			}

			var streamResult = await CreateStream (data, reused, tunnel, cancellationToken).ConfigureAwait (false);
			tunnel = streamResult.tunnel;
			var status = streamResult.status;
			var error = streamResult.error;

			Debug ($"WC INIT CONNECTION #3: Cnc={ID} Op={operation.ID} data={data.ID} - {streamResult.status} {streamResult.success}");
			if (!streamResult.success) {
				if (operation.Aborted)
					return (null, null);

				if (status == WebExceptionStatus.Success && tunnel?.Challenge != null) {
					Debug ($"WC INIT CONNECTION #4: Cnc={ID} Op={operation.ID} data={data.ID}");
					if (tunnel.CloseConnection) {
						data.CloseSocket ();
						oldData = null;
					} else {
						oldData = data;
					}
					goto retry;
				}

				Debug ($"WC INIT CONNECTION #3 EX: Cnc={ID} data={data.ID} socket={data.Socket?.ID}");

				throw GetException (status, error);
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

		internal static bool ReadLine (byte[] buffer, ref int start, int max, ref string output)
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

		internal bool PrepareSharingNtlm (WebOperation operation)
		{
			if (!NtlmAuthenticated)
				return true;

			bool needs_reset = false;
			NetworkCredential cnc_cred = NtlmCredential;
			var request = operation.Request;

			bool isProxy = (request.Proxy != null && !request.Proxy.IsBypassed (request.RequestUri));
			ICredentials req_icreds = (!isProxy) ? request.Credentials : request.Proxy.Credentials;
			NetworkCredential req_cred = (req_icreds != null) ? req_icreds.GetCredential (request.RequestUri, "NTLM") : null;

			if (cnc_cred == null || req_cred == null ||
				cnc_cred.Domain != req_cred.Domain || cnc_cred.UserName != req_cred.UserName ||
				cnc_cred.Password != req_cred.Password) {
				needs_reset = true;
			}

			if (!needs_reset) {
				bool req_sharing = request.UnsafeAuthenticatedConnectionSharing;
				bool cnc_sharing = UnsafeAuthenticatedConnectionSharing;
				needs_reset = (req_sharing == false || req_sharing != cnc_sharing);
			}
			if (needs_reset) {
				Reset (); // closes the authenticated connection
				return false;
			}
			return true;
		}

		internal void Reset ()
		{
			Debug ($"WC RESET: Cnc={ID}");

			lock (this) {
				ResetNtlm ();
				currentData = new WebConnectionData ();
			}
		}

		internal void Close ()
		{
			Debug ($"WC CLOSE: Cnc={ID}");

			lock (this) {
				Data.Close ();
				Reset ();
			}
		}

		internal void ResetNtlm ()
		{
			ntlm_authenticated = false;
			ntlm_credentials = null;
			unsafe_sharing = false;
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

