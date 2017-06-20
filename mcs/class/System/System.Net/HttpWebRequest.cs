//
// System.Net.HttpWebRequest
//
// Authors:
// 	Lawrence Pit (loz@cable.a2000.nl)
// 	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (c) 2002 Lawrence Pit
// (c) 2003 Ximian, Inc. (http://www.ximian.com)
// (c) 2004 Novell, Inc. (http://www.novell.com)
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
#if SECURITY_DEP
#if MONO_SECURITY_ALIAS
extern alias MonoSecurity;
using MonoSecurity::Mono.Security.Interface;
#else
using Mono.Security.Interface;
#endif
#endif

using System;
using System.Collections;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Net.Sockets;
using System.Net.Security;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Serialization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mono.Net.Security;

namespace System.Net
{
	[Serializable]
	public class HttpWebRequest : WebRequest, ISerializable
	{
		Uri requestUri;
		Uri actualUri;
		bool hostChanged;
		bool allowAutoRedirect = true;
		bool allowBuffering = true;
		X509CertificateCollection certificates;
		string connectionGroup;
		bool haveContentLength;
		long contentLength = -1;
		HttpContinueDelegate continueDelegate;
		CookieContainer cookieContainer;
		ICredentials credentials;
		bool haveResponse;
		bool haveRequest;
		bool requestSent;
		WebHeaderCollection webHeaders;
		bool keepAlive = true;
		int maxAutoRedirect = 50;
		string mediaType = String.Empty;
		string method = "GET";
		string initialMethod = "GET";
		bool pipelined = true;
		bool preAuthenticate;
		bool usedPreAuth;
		Version version = HttpVersion.Version11;
		bool force_version;
		Version actualVersion;
		IWebProxy proxy;
		bool sendChunked;
		ServicePoint servicePoint;
		int timeout = 100000;

		WebConnectionStream writeStream;
		HttpWebResponse webResponse;
		TaskCompletionSource<HttpWebResponse> responseTask;
		TaskCompletionSource<WebConnectionData> responseDataTask;
		TaskCompletionSource<Stream> requestTask;
		WebOperation currentOperation;
		int aborted;
		bool gotRequestStream;
		int redirects;
		bool expectContinue;
		byte[] bodyBuffer;
		int bodyBufferLength;
		bool getResponseCalled;
		object locker = new object ();
		bool finished_reading;
		internal WebConnection WebConnection;
		DecompressionMethods auto_decomp;
		int maxResponseHeadersLength;
		static int defaultMaxResponseHeadersLength;
		int readWriteTimeout = 300000; // ms
#if SECURITY_DEP
		MonoTlsProvider tlsProvider;
		MonoTlsSettings tlsSettings;
#endif
		ServerCertValidationCallback certValidationCallback;

		enum NtlmAuthState
		{
			None,
			Challenge,
			Response
		}
		AuthorizationState auth_state, proxy_auth_state;
		string host;

		[NonSerialized]
		internal Action<Stream> ResendContentFactory;

		// Constructors
		static HttpWebRequest ()
		{
			defaultMaxResponseHeadersLength = 64 * 1024;
#if !MOBILE
			NetConfig config = ConfigurationSettings.GetConfig ("system.net/settings") as NetConfig;
			if (config != null) {
				int x = config.MaxResponseHeadersLength;
				if (x != -1)
					x *= 64;

				defaultMaxResponseHeadersLength = x;
			}
#endif
		}

#if MOBILE
		public
#else
		internal
#endif
		HttpWebRequest (Uri uri)
		{
			this.requestUri = uri;
			this.actualUri = uri;
			this.proxy = InternalDefaultWebProxy;
			this.webHeaders = new WebHeaderCollection (WebHeaderCollectionType.HttpWebRequest);
			ThrowOnError = true;
			ResetAuthorization ();
		}

#if SECURITY_DEP
		internal HttpWebRequest (Uri uri, MonoTlsProvider tlsProvider, MonoTlsSettings settings = null)
			: this (uri)
		{
			this.tlsProvider = tlsProvider;
			this.tlsSettings = settings;
		}
#endif

		[Obsolete ("Serialization is obsoleted for this type", false)]
		protected HttpWebRequest (SerializationInfo serializationInfo, StreamingContext streamingContext)
		{
			SerializationInfo info = serializationInfo;

			requestUri = (Uri)info.GetValue ("requestUri", typeof (Uri));
			actualUri = (Uri)info.GetValue ("actualUri", typeof (Uri));
			allowAutoRedirect = info.GetBoolean ("allowAutoRedirect");
			allowBuffering = info.GetBoolean ("allowBuffering");
			certificates = (X509CertificateCollection)info.GetValue ("certificates", typeof (X509CertificateCollection));
			connectionGroup = info.GetString ("connectionGroup");
			contentLength = info.GetInt64 ("contentLength");
			webHeaders = (WebHeaderCollection)info.GetValue ("webHeaders", typeof (WebHeaderCollection));
			keepAlive = info.GetBoolean ("keepAlive");
			maxAutoRedirect = info.GetInt32 ("maxAutoRedirect");
			mediaType = info.GetString ("mediaType");
			method = info.GetString ("method");
			initialMethod = info.GetString ("initialMethod");
			pipelined = info.GetBoolean ("pipelined");
			version = (Version)info.GetValue ("version", typeof (Version));
			proxy = (IWebProxy)info.GetValue ("proxy", typeof (IWebProxy));
			sendChunked = info.GetBoolean ("sendChunked");
			timeout = info.GetInt32 ("timeout");
			redirects = info.GetInt32 ("redirects");
			host = info.GetString ("host");
			ResetAuthorization ();
		}

		static int nextId;
		public readonly int ID = ++nextId;

		void ResetAuthorization ()
		{
			auth_state = new AuthorizationState (this, false);
			proxy_auth_state = new AuthorizationState (this, true);
		}

		// Properties

		void SetSpecialHeaders (string HeaderName, string value)
		{
			value = WebHeaderCollection.CheckBadChars (value, true);
			webHeaders.RemoveInternal (HeaderName);
			if (value.Length != 0) {
				webHeaders.AddInternal (HeaderName, value);
			}
		}

		public string Accept {
			get { return webHeaders["Accept"]; }
			set {
				CheckRequestStarted ();
				SetSpecialHeaders ("Accept", value);
			}
		}

		public Uri Address {
			get { return actualUri; }
			internal set { actualUri = value; } // Used by Ftp+proxy
		}

		public virtual bool AllowAutoRedirect {
			get { return allowAutoRedirect; }
			set { this.allowAutoRedirect = value; }
		}

		public virtual bool AllowWriteStreamBuffering {
			get { return allowBuffering; }
			set { allowBuffering = value; }
		}

		public virtual bool AllowReadStreamBuffering {
			get { return false; }
			set {
				if (value)
					throw new InvalidOperationException ();
			}
		}

		static Exception GetMustImplement ()
		{
			return new NotImplementedException ();
		}

		public DecompressionMethods AutomaticDecompression {
			get {
				return auto_decomp;
			}
			set {
				CheckRequestStarted ();
				auto_decomp = value;
			}
		}

		internal bool InternalAllowBuffering {
			get {
				return allowBuffering && MethodWithBuffer;
			}
		}

		bool MethodWithBuffer {
			get {
				return method != "HEAD" && method != "GET" &&
				method != "MKCOL" && method != "CONNECT" &&
				method != "TRACE";
			}
		}

#if SECURITY_DEP
		internal MonoTlsProvider TlsProvider {
			get { return tlsProvider; }
		}

		internal MonoTlsSettings TlsSettings {
			get { return tlsSettings; }
		}
#endif

		public X509CertificateCollection ClientCertificates {
			get {
				if (certificates == null)
					certificates = new X509CertificateCollection ();
				return certificates;
			}
			set {
				if (value == null)
					throw new ArgumentNullException ("value");
				certificates = value;
			}
		}

		public string Connection {
			get { return webHeaders["Connection"]; }
			set {
				CheckRequestStarted ();

				if (string.IsNullOrEmpty (value)) {
					webHeaders.RemoveInternal ("Connection");
					return;
				}

				string val = value.ToLowerInvariant ();
				if (val.Contains ("keep-alive") || val.Contains ("close"))
					throw new ArgumentException ("Keep-Alive and Close may not be set with this property");

				if (keepAlive)
					value = value + ", Keep-Alive";

				webHeaders.CheckUpdate ("Connection", value);
			}
		}

		public override string ConnectionGroupName {
			get { return connectionGroup; }
			set { connectionGroup = value; }
		}

		public override long ContentLength {
			get { return contentLength; }
			set {
				CheckRequestStarted ();
				if (value < 0)
					throw new ArgumentOutOfRangeException ("value", "Content-Length must be >= 0");

				contentLength = value;
				haveContentLength = true;
			}
		}

		internal long InternalContentLength {
			set { contentLength = value; }
		}

		internal bool ThrowOnError { get; set; }

		public override string ContentType {
			get { return webHeaders["Content-Type"]; }
			set {
				SetSpecialHeaders ("Content-Type", value);
			}
		}

		public HttpContinueDelegate ContinueDelegate {
			get { return continueDelegate; }
			set { continueDelegate = value; }
		}

		virtual
		public CookieContainer CookieContainer {
			get { return cookieContainer; }
			set { cookieContainer = value; }
		}

		public override ICredentials Credentials {
			get { return credentials; }
			set { credentials = value; }
		}
		public DateTime Date {
			get {
				string date = webHeaders["Date"];
				if (date == null)
					return DateTime.MinValue;
				return DateTime.ParseExact (date, "r", CultureInfo.InvariantCulture).ToLocalTime ();
			}
			set {
				SetDateHeaderHelper ("Date", value);
			}
		}

		void SetDateHeaderHelper (string headerName, DateTime dateTime)
		{
			if (dateTime == DateTime.MinValue)
				SetSpecialHeaders (headerName, null); // remove header
			else
				SetSpecialHeaders (headerName, HttpProtocolUtils.date2string (dateTime));
		}

#if !MOBILE
		[MonoTODO]
		public static new RequestCachePolicy DefaultCachePolicy {
			get {
				throw GetMustImplement ();
			}
			set {
				throw GetMustImplement ();
			}
		}
#endif

		[MonoTODO]
		public static int DefaultMaximumErrorResponseLength {
			get {
				throw GetMustImplement ();
			}
			set {
				throw GetMustImplement ();
			}
		}

		public string Expect {
			get { return webHeaders["Expect"]; }
			set {
				CheckRequestStarted ();
				string val = value;
				if (val != null)
					val = val.Trim ().ToLower ();

				if (val == null || val.Length == 0) {
					webHeaders.RemoveInternal ("Expect");
					return;
				}

				if (val == "100-continue")
					throw new ArgumentException ("100-Continue cannot be set with this property.",
								     "value");

				webHeaders.CheckUpdate ("Expect", value);
			}
		}

		virtual
		public bool HaveResponse {
			get { return haveResponse; }
		}

		public override WebHeaderCollection Headers {
			get { return webHeaders; }
			set {
				CheckRequestStarted ();

				WebHeaderCollection webHeaders = value;
				WebHeaderCollection newWebHeaders = new WebHeaderCollection (WebHeaderCollectionType.HttpWebRequest);

				// Copy And Validate -
				// Handle the case where their object tries to change
				//  name, value pairs after they call set, so therefore,
				//  we need to clone their headers.
				//

				foreach (String headerName in webHeaders.AllKeys) {
					newWebHeaders.Add (headerName, webHeaders[headerName]);
				}

				this.webHeaders = newWebHeaders;
			}
		}

		public
		string Host {
			get {
				if (host == null)
					return actualUri.Authority;
				return host;
			}
			set {
				if (value == null)
					throw new ArgumentNullException ("value");

				if (!CheckValidHost (actualUri.Scheme, value))
					throw new ArgumentException ("Invalid host: " + value);

				host = value;
			}
		}

		static bool CheckValidHost (string scheme, string val)
		{
			if (val.Length == 0)
				return false;

			if (val[0] == '.')
				return false;

			int idx = val.IndexOf ('/');
			if (idx >= 0)
				return false;

			IPAddress ipaddr;
			if (IPAddress.TryParse (val, out ipaddr))
				return true;

			string u = scheme + "://" + val + "/";
			return Uri.IsWellFormedUriString (u, UriKind.Absolute);
		}

		public DateTime IfModifiedSince {
			get {
				string str = webHeaders["If-Modified-Since"];
				if (str == null)
					return DateTime.Now;
				try {
					return MonoHttpDate.Parse (str);
				} catch (Exception) {
					return DateTime.Now;
				}
			}
			set {
				CheckRequestStarted ();
				// rfc-1123 pattern
				webHeaders.SetInternal ("If-Modified-Since",
					value.ToUniversalTime ().ToString ("r", null));
				// TODO: check last param when using different locale
			}
		}

		public bool KeepAlive {
			get {
				return keepAlive;
			}
			set {
				keepAlive = value;
			}
		}

		public int MaximumAutomaticRedirections {
			get { return maxAutoRedirect; }
			set {
				if (value <= 0)
					throw new ArgumentException ("Must be > 0", "value");

				maxAutoRedirect = value;
			}
		}

		[MonoTODO ("Use this")]
		public int MaximumResponseHeadersLength {
			get { return maxResponseHeadersLength; }
			set { maxResponseHeadersLength = value; }
		}

		[MonoTODO ("Use this")]
		public static int DefaultMaximumResponseHeadersLength {
			get { return defaultMaxResponseHeadersLength; }
			set { defaultMaxResponseHeadersLength = value; }
		}

		public int ReadWriteTimeout {
			get { return readWriteTimeout; }
			set {
				if (requestSent)
					throw new InvalidOperationException ("The request has already been sent.");

				if (value < -1)
					throw new ArgumentOutOfRangeException ("value", "Must be >= -1");

				readWriteTimeout = value;
			}
		}

		[MonoTODO]
		public int ContinueTimeout {
			get { throw new NotImplementedException (); }
			set { throw new NotImplementedException (); }
		}

		public string MediaType {
			get { return mediaType; }
			set {
				mediaType = value;
			}
		}

		public override string Method {
			get { return this.method; }
			set {
				if (value == null || value.Trim () == "")
					throw new ArgumentException ("not a valid method");

				method = value.ToUpperInvariant ();
				if (method != "HEAD" && method != "GET" && method != "POST" && method != "PUT" &&
					method != "DELETE" && method != "CONNECT" && method != "TRACE" &&
					method != "MKCOL") {
					method = value;
				}
			}
		}

		public bool Pipelined {
			get { return pipelined; }
			set { pipelined = value; }
		}

		public override bool PreAuthenticate {
			get { return preAuthenticate; }
			set { preAuthenticate = value; }
		}

		public Version ProtocolVersion {
			get { return version; }
			set {
				if (value != HttpVersion.Version10 && value != HttpVersion.Version11)
					throw new ArgumentException ("value");

				force_version = true;
				version = value;
			}
		}

		public override IWebProxy Proxy {
			get { return proxy; }
			set {
				CheckRequestStarted ();
				proxy = value;
				servicePoint = null; // we may need a new one
				GetServicePoint ();
			}
		}

		public string Referer {
			get { return webHeaders["Referer"]; }
			set {
				CheckRequestStarted ();
				if (value == null || value.Trim ().Length == 0) {
					webHeaders.RemoveInternal ("Referer");
					return;
				}
				webHeaders.SetInternal ("Referer", value);
			}
		}

		public override Uri RequestUri {
			get { return requestUri; }
		}

		public bool SendChunked {
			get { return sendChunked; }
			set {
				CheckRequestStarted ();
				sendChunked = value;
			}
		}

		public ServicePoint ServicePoint {
			get { return GetServicePoint (); }
		}

		internal ServicePoint ServicePointNoLock {
			get { return servicePoint; }
		}
		public virtual bool SupportsCookieContainer {
			get {
				// The managed implementation supports the cookie container
				// it is only Silverlight that returns false here
				return true;
			}
		}
		public override int Timeout {
			get { return timeout; }
			set {
				if (value < -1)
					throw new ArgumentOutOfRangeException ("value");

				timeout = value;
			}
		}

		public string TransferEncoding {
			get { return webHeaders["Transfer-Encoding"]; }
			set {
				CheckRequestStarted ();
				string val = value;
				if (val != null)
					val = val.Trim ().ToLower ();

				if (val == null || val.Length == 0) {
					webHeaders.RemoveInternal ("Transfer-Encoding");
					return;
				}

				if (val == "chunked")
					throw new ArgumentException ("Chunked encoding must be set with the SendChunked property");

				if (!sendChunked)
					throw new ArgumentException ("SendChunked must be True", "value");

				webHeaders.CheckUpdate ("Transfer-Encoding", value);
			}
		}

		public override bool UseDefaultCredentials {
			get { return CredentialCache.DefaultCredentials == Credentials; }
			set { Credentials = value ? CredentialCache.DefaultCredentials : null; }
		}

		public string UserAgent {
			get { return webHeaders["User-Agent"]; }
			set { webHeaders.SetInternal ("User-Agent", value); }
		}

		bool unsafe_auth_blah;
		public bool UnsafeAuthenticatedConnectionSharing {
			get { return unsafe_auth_blah; }
			set { unsafe_auth_blah = value; }
		}

		internal bool GotRequestStream {
			get { return gotRequestStream; }
		}

		internal bool ExpectContinue {
			get { return expectContinue; }
			set { expectContinue = value; }
		}

		internal Uri AuthUri {
			get { return actualUri; }
		}

		internal bool ProxyQuery {
			get { return servicePoint.UsesProxy && !servicePoint.UseConnect; }
		}

		internal ServerCertValidationCallback ServerCertValidationCallback {
			get { return certValidationCallback; }
		}

		public RemoteCertificateValidationCallback ServerCertificateValidationCallback {
			get {
				if (certValidationCallback == null)
					return null;
				return certValidationCallback.ValidationCallback;
			}
			set {
				if (value == null)
					certValidationCallback = null;
				else
					certValidationCallback = new ServerCertValidationCallback (value);
			}
		}

		// Methods

		internal ServicePoint GetServicePoint ()
		{
			lock (locker) {
				if (hostChanged || servicePoint == null) {
					servicePoint = ServicePointManager.FindServicePoint (actualUri, proxy);
					hostChanged = false;
				}
			}

			return servicePoint;
		}

		public void AddRange (int range)
		{
			AddRange ("bytes", (long)range);
		}

		public void AddRange (int from, int to)
		{
			AddRange ("bytes", (long)from, (long)to);
		}

		public void AddRange (string rangeSpecifier, int range)
		{
			AddRange (rangeSpecifier, (long)range);
		}

		public void AddRange (string rangeSpecifier, int from, int to)
		{
			AddRange (rangeSpecifier, (long)from, (long)to);
		}
		public
		void AddRange (long range)
		{
			AddRange ("bytes", (long)range);
		}

		public
		void AddRange (long from, long to)
		{
			AddRange ("bytes", from, to);
		}

		public
		void AddRange (string rangeSpecifier, long range)
		{
			if (rangeSpecifier == null)
				throw new ArgumentNullException ("rangeSpecifier");
			if (!WebHeaderCollection.IsValidToken (rangeSpecifier))
				throw new ArgumentException ("Invalid range specifier", "rangeSpecifier");

			string r = webHeaders["Range"];
			if (r == null)
				r = rangeSpecifier + "=";
			else {
				string old_specifier = r.Substring (0, r.IndexOf ('='));
				if (String.Compare (old_specifier, rangeSpecifier, StringComparison.OrdinalIgnoreCase) != 0)
					throw new InvalidOperationException ("A different range specifier is already in use");
				r += ",";
			}

			string n = range.ToString (CultureInfo.InvariantCulture);
			if (range < 0)
				r = r + "0" + n;
			else
				r = r + n + "-";
			webHeaders.ChangeInternal ("Range", r);
		}

		public
		void AddRange (string rangeSpecifier, long from, long to)
		{
			if (rangeSpecifier == null)
				throw new ArgumentNullException ("rangeSpecifier");
			if (!WebHeaderCollection.IsValidToken (rangeSpecifier))
				throw new ArgumentException ("Invalid range specifier", "rangeSpecifier");
			if (from > to || from < 0)
				throw new ArgumentOutOfRangeException ("from");
			if (to < 0)
				throw new ArgumentOutOfRangeException ("to");

			string r = webHeaders["Range"];
			if (r == null)
				r = rangeSpecifier + "=";
			else
				r += ",";

			r = String.Format ("{0}{1}-{2}", r, from, to);
			webHeaders.ChangeInternal ("Range", r);
		}

		TaskCompletionSource<WebConnectionData> SendRequest (bool redirecting, CancellationToken cancellationToken)
		{
			lock (locker) {
				WebConnection.Debug ($"HWR SEND REQUEST: {ID} {requestSent} {redirecting}");

				TaskCompletionSource<WebConnectionData> task = null;
				if (!redirecting) {
					if (requestSent) {
						task = responseDataTask;
						if (task == null)
							throw new InvalidOperationException ("Should never happen!");
						return task;
					}

					task = new TaskCompletionSource<WebConnectionData> ();
					if (Interlocked.CompareExchange (ref responseDataTask, task, null) != null)
						throw new InvalidOperationException ("Invalid nested call.");
				}

				var operation = new WebOperation (this, cancellationToken);
				if (Interlocked.CompareExchange (ref currentOperation, operation, null) != null)
					throw new InvalidOperationException ("Invalid nested call.");

				requestSent = true;
				if (!redirecting)
					redirects = 0;
				servicePoint = GetServicePoint ();
				var connection = servicePoint.GetConnection (this, connectionGroup);
				operation.Run (connection);
				return task;
			}
		}

		Task<Stream> MyGetRequestStreamAsync ()
		{
			return RunWithTimeout (MyGetRequestStreamAsync, "The request timed out");
		}

		async Task<Stream> MyGetRequestStreamAsync (Task timeoutTask, CancellationToken cancellationToken)
		{
			if (Aborted)
				throw new WebException ("The request was canceled.", WebExceptionStatus.RequestCanceled);

			bool send = !(method == "GET" || method == "CONNECT" || method == "HEAD" ||
					method == "TRACE");
			if (method == null || !send)
				throw new ProtocolViolationException ("Cannot send data when method is: " + method);

			if (contentLength == -1 && !sendChunked && !allowBuffering && KeepAlive)
				throw new ProtocolViolationException ("Content-Length not set");

			string transferEncoding = TransferEncoding;
			if (!sendChunked && transferEncoding != null && transferEncoding.Trim () != "")
				throw new ProtocolViolationException ("SendChunked should be true.");

			var myTcs = new TaskCompletionSource<Stream> ();
			lock (locker) {
				if (getResponseCalled)
					throw new InvalidOperationException ("The operation cannot be performed once the request has been submitted.");

				var oldTcs = Interlocked.CompareExchange (ref requestTask, myTcs, null);
				if (oldTcs != null)
					throw new InvalidOperationException ("Cannot re-call start of asynchronous " +
								"method while a previous call is still in progress.");

				initialMethod = method;
				if (haveRequest) {
					if (writeStream != null) {
						myTcs.TrySetResult (writeStream);
						return writeStream;
					}
				}

				gotRequestStream = true;
				SendRequest (false, cancellationToken);
			}

			try {
				return await myTcs.Task.ConfigureAwait (false);
			} finally {
				requestTask = null;
			}
		}

		public override IAsyncResult BeginGetRequestStream (AsyncCallback callback, object state)
		{
			if (Aborted)
				throw new WebException ("The request was canceled.", WebExceptionStatus.RequestCanceled);

			return TaskToApm.Begin (MyGetRequestStreamAsync (), callback, state);
		}

		public override Stream EndGetRequestStream (IAsyncResult asyncResult)
		{
			if (asyncResult == null)
				throw new ArgumentNullException ("asyncResult");

			try {
				return TaskToApm.End<Stream> (asyncResult);
			} catch (Exception e) {
				throw FlattenException (e);
			}
		}

		public override Stream GetRequestStream ()
		{
			try {
				return GetRequestStreamAsync ().Result;
			} catch (Exception e) {
				throw FlattenException (e);
			}
		}

		[MonoTODO]
		public Stream GetRequestStream (out TransportContext context)
		{
			throw new NotImplementedException ();
		}

		bool CheckIfForceWrite ()
		{
			if (writeStream == null || writeStream.RequestWritten || !InternalAllowBuffering)
				return false;
			if (contentLength < 0 && writeStream.CanWrite == true && writeStream.WriteBufferLength < 0)
				return false;

			if (contentLength < 0 && writeStream.WriteBufferLength >= 0)
				InternalContentLength = writeStream.WriteBufferLength;

			// This will write the POST/PUT if the write stream already has the expected
			// amount of bytes in it (ContentLength) (bug #77753) or if the write stream
			// contains data and it has been closed already (xamarin bug #1512).

			if (writeStream.WriteBufferLength == contentLength || (contentLength == -1 && writeStream.CanWrite == false))
				return true;

			return false;
		}

		async Task<T> RunWithTimeout<T> (Func<Task, CancellationToken, Task<T>> func, string message)
		{
			using (var cts = new CancellationTokenSource ()) {
				cts.CancelAfter (timeout);
				cts.Token.Register (() => Abort ());
				var timeoutTask = Task.Delay (timeout);
				var workerTask = func (timeoutTask, cts.Token);
				var ret = await Task.WhenAny (workerTask, timeoutTask).ConfigureAwait (false);
				if (ret == timeoutTask)
					throw new WebException (message, WebExceptionStatus.Timeout);
				return workerTask.Result;
			}
		}

		Task<HttpWebResponse> MyGetResponseAsync ()
		{
			return RunWithTimeout (MyGetResponseAsync, "The request timed out");
		}

		async Task<HttpWebResponse> MyGetResponseAsync (Task timeoutTask, CancellationToken cancellationToken)
		{
			if (Aborted)
				throw new WebException ("The request was canceled.", WebExceptionStatus.RequestCanceled);

			if (method == null)
				throw new ProtocolViolationException ("Method is null.");

			string transferEncoding = TransferEncoding;
			if (!sendChunked && transferEncoding != null && transferEncoding.Trim () != "")
				throw new ProtocolViolationException ("SendChunked should be true.");

			bool forceWrite;
			var myTcs = new TaskCompletionSource<HttpWebResponse> ();
			TaskCompletionSource<WebConnectionData> myDataTcs;
			lock (locker) {
				getResponseCalled = true;
				var oldTcs = Interlocked.CompareExchange (ref responseTask, myTcs, null);
				WebConnection.Debug ($"HWR GET RESPONSE: {ID} {oldTcs != null}");
				if (oldTcs != null) {
					if (haveResponse && oldTcs.Task.IsCompleted)
						return oldTcs.Task.Result;
					throw new InvalidOperationException ("Cannot re-call start of asynchronous " +
								"method while a previous call is still in progress.");
				}

				initialMethod = method;
				forceWrite = CheckIfForceWrite ();

				myDataTcs = SendRequest (false, cancellationToken);
			}

			while (true) {
				WebConnectionData data = null;
				WebException throwMe = null;
				HttpWebResponse response = null;
				bool redirect = false;
				bool mustReadAll = false;
				bool ntlm = false;

				try {
					cancellationToken.ThrowIfCancellationRequested ();
					if (forceWrite)
						await writeStream.WriteRequestAsync (cancellationToken).ConfigureAwait (false);

					var anyTask = await Task.WhenAny (timeoutTask, myDataTcs.Task).ConfigureAwait (false);
					if (anyTask == timeoutTask) {
						Abort ();
						throw new WebException ("The request timed out", WebExceptionStatus.Timeout);
					}
					data = myDataTcs.Task.Result;

					/*
					 * WebConnection has either called SetResponseData() or SetResponseError().
					*/

					if (data == null)
						throw new WebException ("Got WebConnectionData == null and no exception.", WebExceptionStatus.ProtocolError);

					(response, redirect, mustReadAll, ntlm) = await GetResponseFromData (data, cancellationToken).ConfigureAwait (false);
				} catch (Exception e) {
					FlattenException (ref e);
					if (Aborted || e is OperationCanceledException)
						throwMe = new WebException ("Request canceled.", WebExceptionStatus.RequestCanceled);
					else
						throwMe = (e as WebException) ?? new WebException (e.Message, e, WebExceptionStatus.ProtocolError, null);
				}

				lock (locker) {
					if (throwMe != null) {
						haveResponse = true;
						myTcs.TrySetException (throwMe);
						throw throwMe;
					}

					if (!redirect) {
						haveResponse = true;
						webResponse = response;
						myTcs.TrySetResult (response);
						return response;
					}

					finished_reading = false;
					haveResponse = false;
					webResponse = null;
					forceWrite = false;
					myDataTcs = new TaskCompletionSource<WebConnectionData> ();
					responseDataTask = myDataTcs;
					currentOperation = null;
					WebConnection.Debug ($"HWR GET RESPONSE ASYNC - REDIRECT: {ID} {mustReadAll} {ntlm}");
				}

				try {
					if (mustReadAll)
						await response.ReadAllAsync (cancellationToken).ConfigureAwait (false);
					response.Close (); 
				} catch (OperationCanceledException) {
					throwMe = new WebException ("Request canceled.", WebExceptionStatus.RequestCanceled);
				} catch (Exception e) {
					FlattenException (ref e);
					throwMe = (e as WebException) ?? new WebException (e.Message, e, WebExceptionStatus.ProtocolError, null);
				}

				lock (locker) {
					if (throwMe != null) {
						haveResponse = true;
						data?.stream?.Close ();
						myTcs.TrySetException (throwMe);
						throw throwMe;
					}

					if (!ntlm) {
						SendRequest (true, cancellationToken);
					}
				}
			}
		}

		async Task<(HttpWebResponse response, bool redirect, bool mustReadAll, bool ntlm)> GetResponseFromData (
			WebConnectionData data, CancellationToken cancellationToken)
		{
			/*
			 * WebConnection has either called SetResponseData() or SetResponseError().
		 	*/

			HttpWebResponse response;
			try {
				if (data == null)
					throw new WebException ("Got WebConnectionData == null and no exception.", WebExceptionStatus.ProtocolError);

				response = new HttpWebResponse (actualUri, method, data, cookieContainer);
			} catch {
				data.stream?.Close ();
				throw;
			}

			WebException throwMe = null;
			bool redirect = false;
			bool mustReadAll = false;
			bool ntlm = false;

			lock (locker) {
				(redirect, mustReadAll, throwMe) = CheckFinalStatus (response);
			}

			if (throwMe != null) {
				if (mustReadAll)
					await response.ReadAllAsync (cancellationToken).ConfigureAwait (false);
				throw throwMe;
			}

			lock (locker) {
				bool isProxy = ProxyQuery && proxy != null && !proxy.IsBypassed (actualUri);

				if (!redirect) {
					if ((isProxy ? proxy_auth_state : auth_state).IsNtlmAuthenticated && (int)response.StatusCode < 400) {
						data.Connection.NtlmAuthenticated = true;
					}

					// clear internal buffer so that it does not
					// hold possible big buffer (bug #397627)
					if (writeStream != null)
						writeStream.KillBuffer ();

					return (response, false, false, false);
				}

				if (sendChunked) {
					sendChunked = false;
					webHeaders.RemoveInternal ("Transfer-Encoding");
				}

				ntlm = HandleNtlmAuth (data, response, cancellationToken);
				WebConnection.Debug ($"HWR REDIRECT: {ntlm} {mustReadAll}");
				if (ntlm)
					mustReadAll = true;
			}

			return (response, true, mustReadAll, ntlm);
		}

		internal static void FlattenException (ref Exception e)
		{
			e = FlattenException (e);
		}

		internal static Exception FlattenException (Exception e)
		{
			if (e is AggregateException ae) {
				ae = ae.Flatten ();
				if (ae.InnerExceptions.Count == 1)
					return ae.InnerException;
			}

			return e;
		}

		public override IAsyncResult BeginGetResponse (AsyncCallback callback, object state)
		{
			if (Aborted)
				throw new WebException ("The request was canceled.", WebExceptionStatus.RequestCanceled);

			return TaskToApm.Begin (MyGetResponseAsync (), callback, state);
		}

		public override WebResponse EndGetResponse (IAsyncResult asyncResult)
		{
			try {
				return TaskToApm.End<HttpWebResponse> (asyncResult);
			} catch (Exception e) {
				throw FlattenException (e);
			}
		}

		public Stream EndGetRequestStream (IAsyncResult asyncResult, out TransportContext context)
		{
			context = null;
			return EndGetRequestStream (asyncResult);
		}

		public override WebResponse GetResponse ()
		{
			try {
				return GetResponseAsync ().Result;
			} catch (Exception e) {
				throw FlattenException (e);
			}
		}

		internal bool FinishedReading {
			get { return finished_reading; }
			set { finished_reading = value; }
		}

		internal bool Aborted {
			get { return Interlocked.CompareExchange (ref aborted, 0, 0) == 1; }
		}

		public override void Abort ()
		{
			if (Interlocked.CompareExchange (ref aborted, 1, 0) == 1)
				return;

			WebConnection.Debug ($"HWR ABORT: {ID}");

			if (haveResponse && finished_reading)
				return;

			haveResponse = true;
			var operation = currentOperation;
			if (operation != null)
				operation.Abort ();

			requestTask?.TrySetCanceled ();
			responseDataTask?.TrySetCanceled ();
			responseTask?.TrySetCanceled ();

			if (writeStream != null) {
				try {
					writeStream.Close ();
					writeStream = null;
				} catch { }
			}

			if (webResponse != null) {
				try {
					webResponse.Close ();
					webResponse = null;
				} catch { }
			}
		}

		void ISerializable.GetObjectData (SerializationInfo serializationInfo,
		   				  StreamingContext streamingContext)
		{
			GetObjectData (serializationInfo, streamingContext);
		}

		protected override void GetObjectData (SerializationInfo serializationInfo,
			StreamingContext streamingContext)
		{
			SerializationInfo info = serializationInfo;

			info.AddValue ("requestUri", requestUri, typeof (Uri));
			info.AddValue ("actualUri", actualUri, typeof (Uri));
			info.AddValue ("allowAutoRedirect", allowAutoRedirect);
			info.AddValue ("allowBuffering", allowBuffering);
			info.AddValue ("certificates", certificates, typeof (X509CertificateCollection));
			info.AddValue ("connectionGroup", connectionGroup);
			info.AddValue ("contentLength", contentLength);
			info.AddValue ("webHeaders", webHeaders, typeof (WebHeaderCollection));
			info.AddValue ("keepAlive", keepAlive);
			info.AddValue ("maxAutoRedirect", maxAutoRedirect);
			info.AddValue ("mediaType", mediaType);
			info.AddValue ("method", method);
			info.AddValue ("initialMethod", initialMethod);
			info.AddValue ("pipelined", pipelined);
			info.AddValue ("version", version, typeof (Version));
			info.AddValue ("proxy", proxy, typeof (IWebProxy));
			info.AddValue ("sendChunked", sendChunked);
			info.AddValue ("timeout", timeout);
			info.AddValue ("redirects", redirects);
			info.AddValue ("host", host);
		}

		void CheckRequestStarted ()
		{
			if (requestSent)
				throw new InvalidOperationException ("request started");
		}

		internal void DoContinueDelegate (int statusCode, WebHeaderCollection headers)
		{
			if (continueDelegate != null)
				continueDelegate (statusCode, headers);
		}

		void RewriteRedirectToGet ()
		{
			method = "GET";
			webHeaders.RemoveInternal ("Transfer-Encoding");
			sendChunked = false;
		}

		bool Redirect (HttpStatusCode code, WebResponse response)
		{
			redirects++;
			Exception e = null;
			string uriString = null;
			switch (code) {
			case HttpStatusCode.Ambiguous: // 300
				e = new WebException ("Ambiguous redirect.");
				break;
			case HttpStatusCode.MovedPermanently: // 301
			case HttpStatusCode.Redirect: // 302
				if (method == "POST")
					RewriteRedirectToGet ();
				break;
			case HttpStatusCode.TemporaryRedirect: // 307
				break;
			case HttpStatusCode.SeeOther: //303
				RewriteRedirectToGet ();
				break;
			case HttpStatusCode.NotModified: // 304
				return false;
			case HttpStatusCode.UseProxy: // 305
				e = new NotImplementedException ("Proxy support not available.");
				break;
			case HttpStatusCode.Unused: // 306
			default:
				e = new ProtocolViolationException ("Invalid status code: " + (int)code);
				break;
			}

			if (method != "GET" && !InternalAllowBuffering && (writeStream.WriteBufferLength > 0 || contentLength > 0))
				e = new WebException ("The request requires buffering data to succeed.", null, WebExceptionStatus.ProtocolError, response);

			if (e != null)
				throw e;

			if (AllowWriteStreamBuffering || method == "GET")
				contentLength = -1;

			uriString = response.Headers["Location"];

			if (uriString == null)
				throw new WebException ("No Location header found for " + (int)code,
							WebExceptionStatus.ProtocolError);

			Uri prev = actualUri;
			try {
				actualUri = new Uri (actualUri, uriString);
			} catch (Exception) {
				throw new WebException (String.Format ("Invalid URL ({0}) for {1}",
									uriString, (int)code),
									WebExceptionStatus.ProtocolError);
			}

			hostChanged = (actualUri.Scheme != prev.Scheme || Host != prev.Authority);
			return true;
		}

		string GetHeaders ()
		{
			bool continue100 = false;
			if (sendChunked) {
				continue100 = true;
				webHeaders.ChangeInternal ("Transfer-Encoding", "chunked");
				webHeaders.RemoveInternal ("Content-Length");
			} else if (contentLength != -1) {
				if (auth_state.NtlmAuthState == NtlmAuthState.Challenge || proxy_auth_state.NtlmAuthState == NtlmAuthState.Challenge) {
					// We don't send any body with the NTLM Challenge request.
					if (haveContentLength || gotRequestStream || contentLength > 0)
						webHeaders.SetInternal ("Content-Length", "0");
					else
						webHeaders.RemoveInternal ("Content-Length");
				} else {
					if (contentLength > 0)
						continue100 = true;

					if (haveContentLength || gotRequestStream || contentLength > 0)
						webHeaders.SetInternal ("Content-Length", contentLength.ToString ());
				}
				webHeaders.RemoveInternal ("Transfer-Encoding");
			} else {
				webHeaders.RemoveInternal ("Content-Length");
			}

			if (actualVersion == HttpVersion.Version11 && continue100 &&
			    servicePoint.SendContinue) { // RFC2616 8.2.3
				webHeaders.ChangeInternal ("Expect", "100-continue");
				expectContinue = true;
			} else {
				webHeaders.RemoveInternal ("Expect");
				expectContinue = false;
			}

			bool proxy_query = ProxyQuery;
			string connectionHeader = (proxy_query) ? "Proxy-Connection" : "Connection";
			webHeaders.RemoveInternal ((!proxy_query) ? "Proxy-Connection" : "Connection");
			Version proto_version = servicePoint.ProtocolVersion;
			bool spoint10 = (proto_version == null || proto_version == HttpVersion.Version10);

			if (keepAlive && (version == HttpVersion.Version10 || spoint10)) {
				if (webHeaders[connectionHeader] == null
				    || webHeaders[connectionHeader].IndexOf ("keep-alive", StringComparison.OrdinalIgnoreCase) == -1)
					webHeaders.ChangeInternal (connectionHeader, "keep-alive");
			} else if (!keepAlive && version == HttpVersion.Version11) {
				webHeaders.ChangeInternal (connectionHeader, "close");
			}

			webHeaders.SetInternal ("Host", Host);
			if (cookieContainer != null) {
				string cookieHeader = cookieContainer.GetCookieHeader (actualUri);
				if (cookieHeader != "")
					webHeaders.ChangeInternal ("Cookie", cookieHeader);
				else
					webHeaders.RemoveInternal ("Cookie");
			}

			string accept_encoding = null;
			if ((auto_decomp & DecompressionMethods.GZip) != 0)
				accept_encoding = "gzip";
			if ((auto_decomp & DecompressionMethods.Deflate) != 0)
				accept_encoding = accept_encoding != null ? "gzip, deflate" : "deflate";
			if (accept_encoding != null)
				webHeaders.ChangeInternal ("Accept-Encoding", accept_encoding);

			if (!usedPreAuth && preAuthenticate)
				DoPreAuthenticate ();

			return webHeaders.ToString ();
		}

		void DoPreAuthenticate ()
		{
			bool isProxy = (proxy != null && !proxy.IsBypassed (actualUri));
			ICredentials creds = (!isProxy || credentials != null) ? credentials : proxy.Credentials;
			Authorization auth = AuthenticationManager.PreAuthenticate (this, creds);
			if (auth == null)
				return;

			webHeaders.RemoveInternal ("Proxy-Authorization");
			webHeaders.RemoveInternal ("Authorization");
			string authHeader = (isProxy && credentials == null) ? "Proxy-Authorization" : "Authorization";
			webHeaders[authHeader] = auth.Message;
			usedPreAuth = true;
		}

		internal void SetWriteStreamError (WebExceptionStatus status, Exception exc)
		{
			if (Aborted)
				return;

			WebConnection.Debug ($"HWR SET WRITE STREAM ERROR: {ID} {requestTask != null} {responseDataTask != null} {responseTask != null}");

			string msg;
			WebException wex;
			if (exc == null) {
				msg = "Error: " + status;
				wex = new WebException (msg, status);
			} else {
				wex = exc as WebException;
				if (wex == null) {
					msg = String.Format ("Error: {0} ({1})", status, exc.Message);
					wex = new WebException (msg, status, WebExceptionInternalStatus.RequestFatal, exc);
				}
			}

			var tcs = requestTask;
			var tcs2 = responseDataTask;

			WebConnection.Debug ($"HWR SET WRITE STREAM ERROR #1: {ID} {tcs != null} {tcs2 != null}");

			if (tcs != null)
				tcs.TrySetException (wex);
			else if (tcs2 != null)
				tcs2.TrySetException (wex);
			else
				WebConnection.Debug ($"HWR SET WRITE STREAM ERROR #2");
		}

		internal byte[] GetRequestHeaders ()
		{
			StringBuilder req = new StringBuilder ();
			string query;
			if (!ProxyQuery) {
				query = actualUri.PathAndQuery;
			} else {
				query = String.Format ("{0}://{1}{2}", actualUri.Scheme,
									Host,
									actualUri.PathAndQuery);
			}

			if (!force_version && servicePoint.ProtocolVersion != null && servicePoint.ProtocolVersion < version) {
				actualVersion = servicePoint.ProtocolVersion;
			} else {
				actualVersion = version;
			}

			req.AppendFormat ("{0} {1} HTTP/{2}.{3}\r\n", method, query,
								actualVersion.Major, actualVersion.Minor);
			req.Append (GetHeaders ());
			string reqstr = req.ToString ();
			return Encoding.UTF8.GetBytes (reqstr);
		}

		internal void SetWriteStream (WebConnectionStream stream)
		{
			try {
				WebConnection.Debug ($"HWR SET WRITE STREAM: {ID}");
				SetWriteStreamAsync (stream, CancellationToken.None).Wait ();
			} catch (Exception ex) {
				Console.Error.WriteLine ($"SET WRITE STREAM FAILED: {ex}");
				SetWriteStreamError (ex);
				throw;
			} finally {
				WebConnection.Debug ($"HWR SET WRITE STREAM DONE: {ID}");
			}
		}

		internal async Task SetWriteStreamAsync (WebConnectionStream stream, CancellationToken cancellationToken)
		{
			if (Aborted)
				return;

			writeStream = stream;
			if (bodyBuffer != null) {
				webHeaders.RemoveInternal ("Transfer-Encoding");
				contentLength = bodyBufferLength;
				writeStream.SendChunked = false;
			}

			try {
				await writeStream.SetHeadersAsync (false, cancellationToken).ConfigureAwait (false);
			} catch (Exception ex) {
				WebConnection.Debug ($"HWR SET WRITE STREAM FAILED: {ID} - {ex}");
				SetWriteStreamError (ex);
				return;
			}

			if (cancellationToken.IsCancellationRequested) {
				SetWriteStreamError (WebExceptionStatus.RequestCanceled, new OperationCanceledException ("HttpWebRequest"));
				return;
			}

			WebConnection.Debug ($"HWR SET WRITE STREAM ASYNC #1: {ID} {bodyBuffer != null} {MethodWithBuffer}");

			haveRequest = true;

			try {
				if (bodyBuffer != null) {
					// The body has been written and buffered. The request "user"
					// won't write it again, so we must do it.
					if (auth_state.NtlmAuthState != NtlmAuthState.Challenge && proxy_auth_state.NtlmAuthState != NtlmAuthState.Challenge) {
						// FIXME: this is a blocking call on the thread pool that could lead to thread pool exhaustion
						await writeStream.WriteAsync (bodyBuffer, 0, bodyBufferLength, cancellationToken).ConfigureAwait (false);
						bodyBuffer = null;
						writeStream.Close ();
					}
				} else if (MethodWithBuffer) {
					if (getResponseCalled && !writeStream.RequestWritten)
						await writeStream.WriteRequestAsync (cancellationToken).ConfigureAwait (false);
				}

				requestTask?.TrySetResult (writeStream);
			} catch (Exception ex) {
				WebConnection.Debug ($"HWR SET WRITE STREAM FAILED #1: {ID} - {ex}");
				SetWriteStreamError (ex);
				return;
			}
		}

		void SetWriteStreamError (Exception exc)
		{
			WebException wexc = exc as WebException;
			if (wexc != null)
				SetWriteStreamError (wexc.Status, wexc);
			else
				SetWriteStreamError (WebExceptionStatus.SendFailure, exc);
		}

		internal void SetResponseError (WebExceptionStatus status, Exception e, string where)
		{
			if (Aborted)
				return;
			lock (locker) {
				string msg = String.Format ("Error getting response stream ({0}): {1}", where, status);
				var tcs = responseDataTask;
				WebConnection.Debug ($"HWR SET RESPONSE ERROR: {ID} {tcs != null}");

				WebException wexc;
				if (e is WebException) {
					wexc = (WebException)e;
				} else {
					wexc = new WebException (msg, e, status, null);
				}
				if (tcs != null) {
					haveResponse = true;
					tcs.TrySetException (wexc);
					return;
				}
			}
		}

		bool HandleNtlmAuth (WebConnectionData data, HttpWebResponse response, CancellationToken cancellationToken)
		{
			bool isProxy = response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired;
			if ((isProxy ? proxy_auth_state : auth_state).NtlmAuthState == NtlmAuthState.None)
				return false;

			data.Connection.PriorityRequest = new WebOperation (this, cancellationToken);
			var creds = (!isProxy || proxy == null) ? credentials : proxy.Credentials;
			if (creds != null) {
				data.Connection.NtlmCredential = creds.GetCredential (requestUri, "NTLM");
				data.Connection.UnsafeAuthenticatedConnectionSharing = unsafe_auth_blah;
			}
			return true;
		}

		internal void SetResponseData (WebConnectionData data)
		{
			lock (locker) {
				var tcs = responseDataTask;

				if (tcs == null) {
					WebConnection.Debug ($"HWR SET RESPONSE DATA - NO TASK: {ID}");
					throw new NotImplementedException ();
				}

				WebConnection.Debug ($"HWR SET RESPONSE DATA: {ID} {Aborted} {tcs.Task.Status} {webResponse != null}");

				if (Aborted) {
					if (data.stream != null)
						data.stream.Close ();
					tcs.TrySetCanceled ();
					return;
				}

				tcs.TrySetResult (data);
				return;
			}
		}

		struct AuthorizationState
		{
			readonly HttpWebRequest request;
			readonly bool isProxy;
			bool isCompleted;
			NtlmAuthState ntlm_auth_state;

			public bool IsCompleted {
				get { return isCompleted; }
			}

			public NtlmAuthState NtlmAuthState {
				get { return ntlm_auth_state; }
			}

			public bool IsNtlmAuthenticated {
				get { return isCompleted && ntlm_auth_state != NtlmAuthState.None; }
			}

			public AuthorizationState (HttpWebRequest request, bool isProxy)
			{
				this.request = request;
				this.isProxy = isProxy;
				isCompleted = false;
				ntlm_auth_state = NtlmAuthState.None;
			}

			public bool CheckAuthorization (WebResponse response, HttpStatusCode code)
			{
				isCompleted = false;
				if (code == HttpStatusCode.Unauthorized && request.credentials == null)
					return false;

				// FIXME: This should never happen!
				if (isProxy != (code == HttpStatusCode.ProxyAuthenticationRequired))
					return false;

				if (isProxy && (request.proxy == null || request.proxy.Credentials == null))
					return false;

				string [] authHeaders = response.Headers.GetValues (isProxy ? "Proxy-Authenticate" : "WWW-Authenticate");
				if (authHeaders == null || authHeaders.Length == 0)
					return false;

				ICredentials creds = (!isProxy) ? request.credentials : request.proxy.Credentials;
				Authorization auth = null;
				foreach (string authHeader in authHeaders) {
					auth = AuthenticationManager.Authenticate (authHeader, request, creds);
					if (auth != null)
						break;
				}
				if (auth == null)
					return false;
				request.webHeaders [isProxy ? "Proxy-Authorization" : "Authorization"] = auth.Message;
				isCompleted = auth.Complete;
				bool is_ntlm = (auth.ModuleAuthenticationType == "NTLM");
				if (is_ntlm)
					ntlm_auth_state = (NtlmAuthState)((int) ntlm_auth_state + 1);
				return true;
			}

			public void Reset ()
			{
				isCompleted = false;
				ntlm_auth_state = NtlmAuthState.None;
				request.webHeaders.RemoveInternal (isProxy ? "Proxy-Authorization" : "Authorization");
			}

			public override string ToString ()
			{
				return string.Format ("{0}AuthState [{1}:{2}]", isProxy ? "Proxy" : "", isCompleted, ntlm_auth_state);
			}
		}

		bool CheckAuthorization (WebResponse response, HttpStatusCode code)
		{
			bool isProxy = code == HttpStatusCode.ProxyAuthenticationRequired;
			return isProxy ? proxy_auth_state.CheckAuthorization (response, code) : auth_state.CheckAuthorization (response, code);
		}

		// Returns true if redirected
		(bool, bool, WebException) CheckFinalStatus (HttpWebResponse response)
		{
			WebException throwMe = null;

			bool mustReadAll = false;
			WebExceptionStatus protoError = WebExceptionStatus.ProtocolError;
			HttpStatusCode code = 0;

			code = response.StatusCode;
			if ((!auth_state.IsCompleted && code == HttpStatusCode.Unauthorized && credentials != null) ||
				(ProxyQuery && !proxy_auth_state.IsCompleted && code == HttpStatusCode.ProxyAuthenticationRequired)) {
				if (!usedPreAuth && CheckAuthorization (response, code)) {
					// Keep the written body, so it can be rewritten in the retry
					if (MethodWithBuffer) {
						if (AllowWriteStreamBuffering) {
							if (writeStream.WriteBufferLength > 0) {
								bodyBuffer = writeStream.WriteBuffer;
								bodyBufferLength = writeStream.WriteBufferLength;
							}

							return (true, false, null);
						}

						//
						// Buffering is not allowed but we have alternative way to get same content (we
						// need to resent it due to NTLM Authentication).
						//
						if (ResendContentFactory != null) {
							using (var ms = new MemoryStream ()) {
								ResendContentFactory (ms);
								bodyBuffer = ms.ToArray ();
								bodyBufferLength = bodyBuffer.Length;
							}
							return (true, false, null);
						}
					} else if (method != "PUT" && method != "POST") {
						bodyBuffer = null;
						return (true, false, null);
					}

					if (!ThrowOnError)
						return (false, false, null);

					writeStream.InternalClose ();
					writeStream = null;
					response.Close ();
					bodyBuffer = null;

					throwMe = new WebException ("This request requires buffering " +
					                            "of data for authentication or " +
					                            "redirection to be sucessful.");
					return (false, false, throwMe);
				}
			}

			bodyBuffer = null;
			if ((int)code >= 400) {
				string err = String.Format ("The remote server returned an error: ({0}) {1}.",
				                            (int)code, response.StatusDescription);
				throwMe = new WebException (err, null, protoError, response);
				mustReadAll = true;
			} else if ((int)code == 304 && allowAutoRedirect) {
				string err = String.Format ("The remote server returned an error: ({0}) {1}.",
				                            (int)code, response.StatusDescription);
				throwMe = new WebException (err, null, protoError, response);
			} else if ((int)code >= 300 && allowAutoRedirect && redirects >= maxAutoRedirect) {
				throwMe = new WebException ("Max. redirections exceeded.", null,
				                            protoError, response);
				mustReadAll = true;
			}
			bodyBuffer = null;

			if (throwMe == null) {
				int c = (int)code;
				bool b = false;
				if (allowAutoRedirect && c >= 300) {
					b = Redirect (code, response);
					if (InternalAllowBuffering && writeStream.WriteBufferLength > 0) {
						bodyBuffer = writeStream.WriteBuffer;
						bodyBufferLength = writeStream.WriteBufferLength;
					}
					if (b && !unsafe_auth_blah) {
						auth_state.Reset ();
						proxy_auth_state.Reset ();
					}
				}

				if (c >= 300 && c != 304)
					mustReadAll = true;

				return (b, mustReadAll, null);
			}

			if (writeStream != null) {
				writeStream.InternalClose ();
				writeStream = null;
			}

			return (false, mustReadAll, throwMe);
		}

		internal bool ReuseConnection {
			get;
			set;
		}

		internal WebConnection StoredConnection;

#region referencesource
        internal static StringBuilder GenerateConnectionGroup(string connectionGroupName, bool unsafeConnectionGroup, bool isInternalGroup)
        {
            StringBuilder connectionLine = new StringBuilder(connectionGroupName);

            connectionLine.Append(unsafeConnectionGroup ? "U>" : "S>");

            if (isInternalGroup)
            {
                connectionLine.Append("I>");
            }

            return connectionLine;
        }
#endregion
	}
}

