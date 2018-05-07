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
}

namespace System.Buffers.Text
{
	class Utf8Formatter
	{
		public static bool TryFormat (bool value, Span<byte> destination, out int bytesWritten)
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
