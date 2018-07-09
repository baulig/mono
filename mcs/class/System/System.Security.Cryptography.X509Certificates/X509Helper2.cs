//
// X509Helper2.cs
//
// Authors:
//	Martin Baulig  <martin.baulig@xamarin.com>
//
// Copyright (C) 2016 Xamarin, Inc. (http://www.xamarin.com)
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

#if SECURITY_DEP
#if MONO_SECURITY_ALIAS
extern alias MonoSecurity;
#endif

#if MONO_SECURITY_ALIAS
using MX = MonoSecurity::Mono.Security.X509;
#else
using MX = Mono.Security.X509;
#endif
#endif

using System.IO;
using System.Text;
using Mono;

namespace System.Security.Cryptography.X509Certificates
{
	internal static class X509Helper2
	{
#if SECURITY_DEP
		/*
		 * This is used by X509ChainImplMono
		 * 
		 * Some of the missing APIs such as X509v3 extensions can be added to the native
		 * BTLS implementation.
		 * 
		 * We should also consider replacing X509ChainImplMono with a new X509ChainImplBtls
		 * at some point.
		 */
		[MonoTODO ("Investigate replacement; see comments in source.")]
		internal static MX.X509Certificate GetMonoCertificate (X509Certificate2 certificate)
		{
			if (certificate.Impl is X509Certificate2ImplMono monoImpl)
				return monoImpl.MonoCertificate;
			if (certificate.Impl is X509Certificate2Impl impl2 && impl2.FallbackImpl is X509Certificate2ImplMono fallbackImpl)
				return fallbackImpl.MonoCertificate;
			
			var impl = SystemDependencyProvider.Instance.CertificateProvider.Import (certificate, CertificateImportFlags.DisableNativeBackend);
			if (impl is X509Certificate2ImplMono fallbackImpl2)
				return fallbackImpl2.MonoCertificate;
			throw new NotSupportedException ();
		}

		internal static X509ChainImpl CreateChainImpl (bool useMachineContext)
		{
			return new X509ChainImplMono (useMachineContext);
		}

		public static bool IsValid (X509ChainImpl impl)
		{
			return impl != null && impl.IsValid;
		}

		internal static void ThrowIfContextInvalid (X509ChainImpl impl)
		{
			if (!IsValid (impl))
				throw GetInvalidChainContextException ();
		}

		internal static Exception GetInvalidChainContextException ()
		{
			return new CryptographicException (Locale.GetText ("Chain instance is empty."));
		}
#endif
	}
}
