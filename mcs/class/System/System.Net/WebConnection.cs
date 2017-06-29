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
		object socketLock = new object ();
		bool keepAlive;
		NetworkCredential ntlm_credentials;
		bool ntlm_authenticated;
		bool unsafe_sharing;

		WebConnectionData currentData;
		WebConnectionData Data {
			get { return currentData; }
		}

		public ServicePoint ServicePoint {
			get;
		}

#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
		[System.Runtime.InteropServices.DllImport ("__Internal")]
		static extern void xamarin_start_wwan (string uri);
#endif

		public WebConnection (ServicePoint sPoint)
		{
			ServicePoint = sPoint;
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

		bool CheckReusable (WebConnectionData data)
		{
			if (data == null)
				return false;

			if (data.Socket != null && data.Socket.Connected) {
				try {
					if (CanReuse (data.Socket))
						return true;
				} catch {
					return false;
				}
			}

			if (data.Socket != null) {
				data.Socket.Close ();
				data.Socket = null;
			}

			return false;
		}

		async Task<Socket> Connect (WebOperation operation, WebConnectionData data, CancellationToken cancellationToken)
		{
			IPHostEntry hostEntry = ServicePoint.HostEntry;

			if (hostEntry == null || hostEntry.AddressList.Length == 0) {
#if MONOTOUCH && !MONOTOUCH_TV && !MONOTOUCH_WATCH
					xamarin_start_wwan (ServicePoint.Address.ToString ());
					hostEntry = ServicePoint.HostEntry;
					if (hostEntry == null) {
#endif
				throw GetException (ServicePoint.UsesProxy ? WebExceptionStatus.ProxyNameResolutionFailure :
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
				IPEndPoint remote = new IPEndPoint (address, ServicePoint.Address.Port);
				socket.NoDelay = !ServicePoint.UseNagleAlgorithm;
				try {
					ServicePoint.KeepAliveSetup (socket);
				} catch {
					// Ignore. Not supported in all platforms.
				}

				if (!ServicePoint.CallEndPointDelegate (socket, remote)) {
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

		async Task<(bool success, WebConnectionTunnel tunnel)> CreateStream (
			WebConnectionData data, bool reused, WebConnectionTunnel tunnel, CancellationToken cancellationToken)
		{
			var requestID = ++nextRequestID;

			try {
				NetworkStream serverStream = new NetworkStream (data.Socket, false);

				Debug ($"WC CREATE STREAM: Cnc={ID} data={data.ID} {requestID} {reused} socket={data.Socket.ID}");

				if (data.Request.Address.Scheme == Uri.UriSchemeHttps) {
					if (!reused || data.NetworkStream == null || data.MonoTlsStream == null) {
						if (ServicePoint.UseConnect) {
							if (tunnel == null)
								tunnel = new WebConnectionTunnel (data.Request, ServicePoint.Address);
							await tunnel.Initialize (serverStream, cancellationToken).ConfigureAwait (false);
							if (!tunnel.Success)
								return (false, tunnel);
						}
						await data.Initialize (serverStream, tunnel, cancellationToken).ConfigureAwait (false);
					}
					return (true, tunnel);
				}

				data.Initialize (serverStream);
				return (true, null);
			} catch (Exception ex) {
				ex = HttpWebRequest.FlattenException (ex);
				Debug ($"WC CREATE STREAM EX: Cnc={ID} {requestID} {data.Request.Aborted} - {ex.Message}");
				if (data.Request.Aborted || data.MonoTlsStream == null)
					throw GetException (WebExceptionStatus.ConnectFailure, ex);
				throw GetException (data.MonoTlsStream.ExceptionStatus, ex);
			} finally {
				Debug ($"WC CREATE STREAM DONE: Cnc={ID} {requestID}");
			}
		}

		internal async Task<(WebConnectionData, WebRequestStream)> InitConnection (
			WebOperation operation, CancellationToken cancellationToken)
		{
			Debug ($"WC INIT CONNECTION: Cnc={ID} Req={operation.Request.ID} Op={operation.ID}");

			var request = operation.Request;

			operation.ThrowIfClosedOrDisposed (cancellationToken);

			keepAlive = request.KeepAlive;
			var data = new WebConnectionData (this, operation);
			var oldData = Interlocked.Exchange (ref currentData, data);
			WebConnectionTunnel tunnel = null;

		retry:
			operation.ThrowIfClosedOrDisposed (cancellationToken);

			bool reused = CheckReusable (oldData);
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

			bool success;
			(success, tunnel) = await CreateStream (data, reused, tunnel, cancellationToken).ConfigureAwait (false);

			Debug ($"WC INIT CONNECTION #3: Cnc={ID} Op={operation.ID} data={data.ID} - {success}");
			if (!success) {
				if (operation.Aborted)
					return (null, null);

				if (tunnel?.Challenge == null)
					throw GetException (WebExceptionStatus.ProtocolError, null);

				Debug ($"WC INIT CONNECTION #4: Cnc={ID} Op={operation.ID} data={data.ID}");
				if (tunnel.CloseConnection) {
					data.CloseSocket ();
					oldData = null;
				} else {
					oldData = data;
				}
				goto retry;
			}

			var stream = new WebRequestStream (this, operation, data.NetworkStream, tunnel);
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

		internal void FinishOperation (ref bool keepAlive)
		{
			lock (this) {
				if (Data == null) {
					keepAlive = false;
					return;
				}

				if (!keepAlive || Data.Socket == null || !Data.Socket.Connected) {
					try {
						Data.Close ();
					} catch { }
					keepAlive = false;
					currentData = null;
					ResetNtlm ();
				}
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

