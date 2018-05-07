//
// MonoAuthenticationOptions.cs
//
// Author:
//       Martin Baulig <mabaul@microsoft.com>
//
// Copyright (c) 2018 Xamarin Inc. (http://www.xamarin.com)
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

#if SECURITY_DEP

#if MONO_SECURITY_ALIAS
extern alias MonoSecurity;
#endif

#if MONO_SECURITY_ALIAS
using MonoSecurity::Mono.Security.Interface;
#else
using Mono.Security.Interface;
#endif
#endif

using System;
using System.Net.Security;
using System.Security.Authentication;

namespace Mono.Net.Security
{
	static class MonoAuthenticationOptions
	{
		public static IMonoSslClientAuthenticationOptions Wrap (SslClientAuthenticationOptions options)
		{
			return options != null ? new ClientWrapper (options) : null;
		}

		public static SslClientAuthenticationOptions Unwrap (IMonoSslClientAuthenticationOptions options)
		{
			return options != null ? ((ClientWrapper)options).Options : null;
		}

		public static IMonoSslServerAuthenticationOptions Wrap (SslServerAuthenticationOptions options)
		{
			return options != null ? new ServerWrapper (options) : null;
		}

		public static SslServerAuthenticationOptions Unwrap (IMonoSslServerAuthenticationOptions options)
		{
			return options != null ? ((ServerWrapper)options).Options : null;
		}

		class ClientWrapper : IMonoSslClientAuthenticationOptions
		{
			public SslClientAuthenticationOptions Options {
				get;
			}

			public ClientWrapper (SslClientAuthenticationOptions options)
			{
				Options = options;
			}
		}

		class ServerWrapper : IMonoSslServerAuthenticationOptions
		{
			public SslServerAuthenticationOptions Options {
				get;
			}

			public ServerWrapper (SslServerAuthenticationOptions options)
			{
				Options = options;
			}
		}
	}
}
