using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
	partial class HttpClientHandler
	{
		protected internal override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
		{
			throw new NotImplementedException ();
		}
	}

	partial class AuthenticationHelper
	{
		static Task<HttpResponseMessage> SendWithNtAuthAsync (HttpRequestMessage request, Uri authUri, ICredentials credentials, bool isProxyAuth, HttpConnection connection, CancellationToken cancellationToken)
		{
			throw new NotImplementedException ();
		}

		public static Task<HttpResponseMessage> SendWithNtProxyAuthAsync (HttpRequestMessage request, Uri proxyUri, ICredentials proxyCredentials, HttpConnection connection, CancellationToken cancellationToken)
		{
			return SendWithNtAuthAsync (request, proxyUri, proxyCredentials, isProxyAuth: true, connection, cancellationToken);
		}

		public static Task<HttpResponseMessage> SendWithNtConnectionAuthAsync (HttpRequestMessage request, ICredentials credentials, HttpConnection connection, CancellationToken cancellationToken)
		{
			return SendWithNtAuthAsync (request, request.RequestUri, credentials, isProxyAuth: false, connection, cancellationToken);
		}
	}
}

namespace System.Buffers.Text
{
	class Utf8Formatter
	{
		public static bool TryFormat (bool value, Span<byte> destination, out int bytesWritten)
		{
			throw new NotImplementedException ();
		}

		public static bool TryFormat (int value, Span<byte> destination, out int bytesWritten)
		{
			throw new NotImplementedException ();
		}

	}

	class Utf8Parser
	{
		public static bool TryParse ()
		{
			throw new NotImplementedException ();
		}
	}
}

namespace System.IO
{
	static class StreamExtensions
	{
		public static Task CopyToAsync (this Stream stream, Stream destination, CancellationToken cancellationToken)
		{
			return stream.CopyToAsync (destination, 81920, cancellationToken);
		}
	}
}

namespace System.Text
{
	static class EncodingHelper
	{
		public static int GetBytes (this Encoding encoding, string s, byte[] bytes)
		{
			return encoding.GetBytes (s, 0, s.Length, bytes, 0);
		}
	}
}
