// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
	public partial class HttpClientHandler : HttpMessageHandler
	{
		readonly SocketsHttpHandler socketsHttpHandler;
		readonly DiagnosticsHandler diagnosticsHandler;
		ClientCertificateOption clientCertificateOptions;

		public HttpClientHandler ()
		{
			socketsHttpHandler = new SocketsHttpHandler ();
			diagnosticsHandler = new DiagnosticsHandler (socketsHttpHandler);
			clientCertificateOptions = ClientCertificateOption.Manual;
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing && !_disposed) {
				_disposed = true;
				socketsHttpHandler.Dispose ();
			}

			base.Dispose (disposing);
		}

		public virtual bool SupportsAutomaticDecompression => true;
		public virtual bool SupportsProxy => true;
		public virtual bool SupportsRedirectConfiguration => true;

		public bool UseCookies {
			get => socketsHttpHandler.UseCookies;
			set {
				socketsHttpHandler.UseCookies = value;
			}
		}

		public CookieContainer CookieContainer {
			get => socketsHttpHandler.CookieContainer;
			set {
				socketsHttpHandler.CookieContainer = value;
			}
		}

		public DecompressionMethods AutomaticDecompression {
			get => socketsHttpHandler.AutomaticDecompression;
			set {
				socketsHttpHandler.AutomaticDecompression = value;
			}
		}

		public bool UseProxy {
			get => socketsHttpHandler.UseProxy;
			set {
				socketsHttpHandler.UseProxy = value;
			}
		}

		public IWebProxy Proxy {
			get => socketsHttpHandler.Proxy;
			set {
				socketsHttpHandler.Proxy = value;
			}
		}

		public ICredentials DefaultProxyCredentials {
			get => socketsHttpHandler.DefaultProxyCredentials;
			set {
				socketsHttpHandler.DefaultProxyCredentials = value;
			}
		}

		public bool PreAuthenticate {
			get => socketsHttpHandler.PreAuthenticate;
			set {
				socketsHttpHandler.PreAuthenticate = value;
			}
		}

		public bool UseDefaultCredentials {
			// WinHttpHandler doesn't have a separate UseDefaultCredentials property.  There
			// is just a ServerCredentials property.  So, we need to map the behavior.
			// Do the same for SocketsHttpHandler.Credentials.
			//
			// This property only affect .ServerCredentials and not .DefaultProxyCredentials.

			get => socketsHttpHandler.Credentials == CredentialCache.DefaultCredentials;
			set {
				if (value) {
					socketsHttpHandler.Credentials = CredentialCache.DefaultCredentials;
				} else {
					if (socketsHttpHandler.Credentials == CredentialCache.DefaultCredentials) {
						// Only clear out the Credentials property if it was a DefaultCredentials.
						socketsHttpHandler.Credentials = null;
					}
				}
			}
		}

		public ICredentials Credentials {
			get => socketsHttpHandler.Credentials;
			set {
				socketsHttpHandler.Credentials = value;
			}
		}

		public bool AllowAutoRedirect {
			get => socketsHttpHandler.AllowAutoRedirect;
			set {
				socketsHttpHandler.AllowAutoRedirect = value;
			}
		}

		public int MaxAutomaticRedirections {
			get => socketsHttpHandler.MaxAutomaticRedirections;
			set {
				socketsHttpHandler.MaxAutomaticRedirections = value;
			}
		}

		public int MaxConnectionsPerServer {
			get => socketsHttpHandler.MaxConnectionsPerServer;
			set {
				socketsHttpHandler.MaxConnectionsPerServer = value;
			}
		}

		public int MaxResponseHeadersLength {
			get => socketsHttpHandler.MaxResponseHeadersLength;
			set {
				socketsHttpHandler.MaxResponseHeadersLength = value;
			}
		}

		public ClientCertificateOption ClientCertificateOptions {
			get {
				return clientCertificateOptions;
			}
			set {
				throw new ArgumentOutOfRangeException (nameof (value));
			}
		}

		public X509CertificateCollection ClientCertificates {
			get {
				if (ClientCertificateOptions != ClientCertificateOption.Manual) {
					throw new InvalidOperationException (SR.Format (SR.net_http_invalid_enable_first, nameof (ClientCertificateOptions), nameof (ClientCertificateOption.Manual)));
				}

				return socketsHttpHandler.SslOptions.ClientCertificates ??
				    (socketsHttpHandler.SslOptions.ClientCertificates = new X509CertificateCollection ());
			}
		}

		public Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool> ServerCertificateCustomValidationCallback {
			get {
				return (socketsHttpHandler.SslOptions.RemoteCertificateValidationCallback?.Target as ConnectHelper.CertificateCallbackMapper)?.FromHttpClientHandler;
			}
			set {
				socketsHttpHandler.SslOptions.RemoteCertificateValidationCallback = value != null ?
				    new ConnectHelper.CertificateCallbackMapper (value).ForSocketsHttpHandler :
				    null;
			}
		}

		public bool CheckCertificateRevocationList {
			get => socketsHttpHandler.SslOptions.CertificateRevocationCheckMode == X509RevocationMode.Online;
			set {
				socketsHttpHandler.SslOptions.CertificateRevocationCheckMode = value ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
			}
		}

		public SslProtocols SslProtocols {
			get => socketsHttpHandler.SslOptions.EnabledSslProtocols;
			set {
				socketsHttpHandler.SslOptions.EnabledSslProtocols = value;
			}
		}

		public IDictionary<string, object> Properties => socketsHttpHandler.Properties;

		protected internal override Task<HttpResponseMessage> SendAsync (HttpRequestMessage request, CancellationToken cancellationToken)
		{
			return DiagnosticsHandler.IsEnabled () ?
			      diagnosticsHandler.SendAsync (request, cancellationToken) :
			      socketsHttpHandler.SendAsync (request, cancellationToken);
		}
	}
}
