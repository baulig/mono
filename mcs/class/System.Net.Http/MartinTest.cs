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
			return false;
		}
	}
}