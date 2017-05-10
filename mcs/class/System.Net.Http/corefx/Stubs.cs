namespace System.Net.Http {
	enum ClientCertificateOption
	{
		Automatic = 1,
		Manual = 0,
	}

	public enum HttpCompletionOption
	{
		ResponseContentRead = 0,
		ResponseHeadersRead = 1,
	}


	partial class HttpResponseMessage
	{
	}

	partial class HttpRequestException : Exception
	{
		public HttpRequestException () { }
		public HttpRequestException (string message) { }
		public HttpRequestException (string message, System.Exception inner) { }
	}

	partial class HttpMethod {
	}

}
namespace System.Net.Http.Headers {
}

