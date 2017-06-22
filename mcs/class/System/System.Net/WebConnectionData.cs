//
// System.Net.WebConnectionData
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

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using Mono.Net.Security;

namespace System.Net
{
	class WebConnectionData
	{
		public int StatusCode;
		public string StatusDescription;
		public WebHeaderCollection Headers;
		public Version Version;
		public Version ProxyVersion;
		public Stream stream;
		public string[] Challenge;
		public MonoChunkStream ChunkStream;
		ReadState _readState;
		Stream networkStream;
		Socket socket;
		MonoTlsStream tlsStream;

		static int nextID;
		public readonly int ID = ++nextID;

		public WebConnectionData ()
		{
			_readState = ReadState.None;
		}

		public WebConnectionData (WebConnection connection, WebOperation operation)
		{
			Connection = connection;
			Operation = operation;
			Request = operation.Request;
		}

		public void ReuseConnection (WebConnectionData old)
		{
			lock (this) {
				socket = Interlocked.Exchange (ref old.socket, null);
				networkStream = Interlocked.Exchange (ref old.networkStream, null);
				tlsStream = Interlocked.Exchange (ref old.tlsStream, null);
				old.ChunkStream = null;
			}
		}

		public WebConnection Connection {
			get;
		}

		public WebOperation Operation {
			get;
		}

		public HttpWebRequest Request {
			get;
		}

		public ReadState ReadState {
			get {
				return _readState;
			}
			set {
				lock (this) {
					if ((_readState == ReadState.Aborted) && (value != ReadState.Aborted))
						throw new WebException ("Aborted", WebExceptionStatus.RequestCanceled);
					_readState = value;
				}
			}
		}

		public Stream NetworkStream {
			get {
				lock (this) {
					if (false && ReadState == ReadState.Aborted)
						throw new WebException ("Aborted", WebExceptionStatus.RequestCanceled);
					return networkStream;
				}
			}
		}

		public Socket Socket {
			get { return socket; }
			set { socket = value; }
		}

		public void Close ()
		{
			lock (this) {
				if (networkStream != null) {
					try {
						networkStream.Close ();
					} catch { }
					networkStream = null;
				}

				if (socket != null) {
					try {
						socket.Close ();
					} catch { }
					socket = null;
				}

				_readState = ReadState.Aborted;
			}
		}

		public MonoTlsStream MonoTlsStream {
			get { return tlsStream; }
		}

		public void Initialize (NetworkStream stream)
		{
			networkStream = stream;
		}

#if SECURITY_DEP
		public async Task Initialize (NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
		{
			tlsStream = new MonoTlsStream (Request, stream);
			networkStream = await tlsStream.CreateStream (buffer, cancellationToken).ConfigureAwait (false);
		}
#endif
	}
}

