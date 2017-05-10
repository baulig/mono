//
// IHttpWebRequestInternal.cs
//
// Author:
//       Martin Baulig <mabaul@microsoft.com>
//
// Copyright (c) 2017 Xamarin Inc. (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
namespace System.Net
{
	interface IHttpConnectionInternal
	{
		void Close (bool sendNext);

		void ResetNtlm ();

		EventHandler SendRequest (IHttpWebRequestInternal request);

		bool NtlmAuthenticated {
			get; set;
		}

		NetworkCredential NtlmCredential {
			get; set;
		}

		bool UnsafeAuthenticatedConnectionSharing {
			get; set;
		}
	}

	interface IHttpWebRequestInternal
	{
		IWebProxy Proxy {
			get; set;
		}

		Uri RequestUri {
			get;
		}

		ICredentials Credentials {
			get; set;
		}

		bool UnsafeAuthenticatedConnectionSharing {
			get; set;
		}

		bool Aborted {
			get;
		}

		string Method {
			get; set;
		}

		Uri Address {
			get;
		}

		WebHeaderCollection Headers {
			get; set;
		}

		IHttpConnectionInternal CreateConnection (IWebConnectionState state, ServicePoint sPoint);
	}
}
