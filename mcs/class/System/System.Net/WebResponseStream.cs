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
		public WebResponseStream (WebConnection connection, WebOperation operation, WebConnectionData data)
			: base (connection, operation, data)
		{
		}

		internal async Task<int> ReadAsync (WebOperation operation, WebConnectionData data,
		                                    byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			WebConnection.Debug ($"WC READ ASYNC: {Connection.ID}");

			operation.ThrowIfDisposed (cancellationToken);
			var s = data.NetworkStream;
			if (s == null)
				throw new ObjectDisposedException (typeof (NetworkStream).FullName);

			int nbytes = 0;
			bool done = false;

			if (!data.ChunkedRead || (!data.ChunkStream.DataAvailable && data.ChunkStream.WantMore)) {
				nbytes = await s.ReadAsync (buffer, offset, size, cancellationToken).ConfigureAwait (false);
				WebConnection.Debug ($"WC READ ASYNC #1: {Connection.ID} {nbytes} {data.ChunkedRead}");
				if (!data.ChunkedRead)
					return nbytes;
				done = nbytes == 0;
			}

			try {
				data.ChunkStream.WriteAndReadBack (buffer, offset, size, ref nbytes);
				WebConnection.Debug ($"WC READ ASYNC #1: {Connection.ID} {done} {nbytes} {data.ChunkStream.WantMore}");
				if (!done && nbytes == 0 && data.ChunkStream.WantMore)
					nbytes = await EnsureReadAsync (data, buffer, offset, size, cancellationToken).ConfigureAwait (false);
			} catch (Exception e) {
				if (e is WebException || e is OperationCanceledException)
					throw;
				throw new WebException ("Invalid chunked data.", e, WebExceptionStatus.ServerProtocolViolation, null);
			}

			if ((done || nbytes == 0) && data.ChunkStream.ChunkLeft != 0) {
				// HandleError (WebExceptionStatus.ReceiveFailure, null, "chunked EndRead");
				throw new WebException ("Read error", null, WebExceptionStatus.ReceiveFailure, null);
			}

			return nbytes;
		}

		async Task<int> EnsureReadAsync (WebConnectionData data, byte[] buffer, int offset, int size, CancellationToken cancellationToken)
		{
			byte[] morebytes = null;
			int nbytes = 0;
			while (nbytes == 0 && data.ChunkStream.WantMore && !cancellationToken.IsCancellationRequested) {
				int localsize = data.ChunkStream.ChunkLeft;
				if (localsize <= 0) // not read chunk size yet
					localsize = 1024;
				else if (localsize > 16384)
					localsize = 16384;

				if (morebytes == null || morebytes.Length < localsize)
					morebytes = new byte[localsize];

				int nread = await data.NetworkStream.ReadAsync (morebytes, 0, localsize, cancellationToken).ConfigureAwait (false);
				if (nread <= 0)
					return 0; // Error

				data.ChunkStream.Write (morebytes, 0, nread);
				nbytes += data.ChunkStream.Read (buffer, offset + nbytes, size - nbytes);
			}

			return nbytes;
		}

		protected override void Close_internal (ref bool disposed)
		{
			
		}
	}
}
