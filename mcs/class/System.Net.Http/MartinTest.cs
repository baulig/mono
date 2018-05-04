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
