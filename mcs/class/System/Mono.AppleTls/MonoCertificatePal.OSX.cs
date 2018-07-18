//
// MonoCertificatePal.OSX.cs
//
// Authors:
//       Miguel de Icaza
//       Sebastien Pouliot <sebastien@xamarin.com>
//       Martin Baulig <mabaul@microsoft.com>
//
// Copyright 2010 Novell, Inc
// Copyright 2011-2014 Xamarin Inc.
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
using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Apple;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using ObjCRuntimeInternal;
using Mono.Net;

#if MONO_FEATURE_BTLS
using Mono.Btls;
#else
using Mono.Security.Cryptography;
#endif

namespace Mono.AppleTls
{
	using Interop = global::Interop;

	static partial class MonoCertificatePal
	{
		public static X509ContentType GetCertContentType (byte[] rawData)
		{
			if (rawData == null || rawData.Length == 0)
				return X509ContentType.Unknown;

			return Interop.AppleCrypto.X509GetContentType (rawData, rawData.Length);
		}

		public static SafeHandle XFromBlob (byte[] rawData, SafePasswordHandle password, X509KeyStorageFlags keyStorageFlags)
		{
			Debug.Assert (password != null);

			X509ContentType contentType = GetCertContentType (rawData);

			if (contentType == X509ContentType.Pkcs7) {
				// In single mode for a PKCS#7 signed or signed-and-enveloped file we're supposed to return
				// the certificate which signed the PKCS#7 file.
				// 
				// X509Certificate2Collection::Export(X509ContentType.Pkcs7) claims to be a signed PKCS#7,
				// but doesn't emit a signature block. So this is hard to test.
				//
				// TODO(2910): Figure out how to extract the signing certificate, when it's present.
				throw new CryptographicException (SR.Cryptography_X509_PKCS7_NoSigner);
			}

			bool exportable = true;

			SafeKeychainHandle keychain;

			if (contentType == X509ContentType.Pkcs12) {
				if ((keyStorageFlags & X509KeyStorageFlags.EphemeralKeySet) == X509KeyStorageFlags.EphemeralKeySet)
					throw new PlatformNotSupportedException (SR.Cryptography_X509_NoEphemeralPfx);

				exportable = (keyStorageFlags & X509KeyStorageFlags.Exportable) == X509KeyStorageFlags.Exportable;

				bool persist =
				    (keyStorageFlags & X509KeyStorageFlags.PersistKeySet) == X509KeyStorageFlags.PersistKeySet;

				keychain = persist
				    ? Interop.AppleCrypto.SecKeychainCopyDefault ()
				    : Interop.AppleCrypto.CreateTemporaryKeychain ();
			} else {
				keychain = SafeTemporaryKeychainHandle.InvalidHandle;
				password = SafePasswordHandle.InvalidHandle;
			}

			using (keychain) {
				SafeSecIdentityHandle identityHandle;
				SafeSecCertificateHandle certHandle = Interop.AppleCrypto.X509ImportCertificate (
				    rawData,
				    contentType,
				    password,
				    keychain,
				    exportable,
				    out identityHandle);

				if (identityHandle.IsInvalid) {
					identityHandle.Dispose ();
					// return new AppleCertificatePal (certHandle);
					return certHandle;
				}

				if (contentType != X509ContentType.Pkcs12) {
					Debug.Fail ("Non-PKCS12 import produced an identity handle");

					identityHandle.Dispose ();
					certHandle.Dispose ();
					throw new CryptographicException ();
				}

				Debug.Assert (certHandle.IsInvalid);
				certHandle.Dispose ();
				return identityHandle;
				// return new AppleCertificatePal (identityHandle);
			}
		}

		public static SafeSecIdentityHandle ImportIdentity (X509Certificate2 certificate)
		{
			if (certificate == null)
				throw new ArgumentNullException (nameof (certificate));
			if (!certificate.HasPrivateKey)
				throw new InvalidOperationException ("Need X509Certificate2 with a private key.");

			return ItemImport (certificate) ?? new SafeSecIdentityHandle ();
		}

		[DllImport (AppleTlsContext.SecurityLibrary)]
		extern static /* SecIdentityRef */ IntPtr SecIdentityCreate (
			/* CFAllocatorRef */ IntPtr allocator,
			/* SecCertificateRef */ IntPtr certificate,
			/* SecKeyRef */ IntPtr privateKey);

		static public SafeSecIdentityHandle ItemImport (X509Certificate2 certificate)
		{
			if (!certificate.HasPrivateKey)
				throw new NotSupportedException ();

			using (var key = ImportPrivateKey (certificate))
			using (var cert = MonoCertificatePal.FromOtherCertificate (certificate)) {
				var identity = SecIdentityCreate (IntPtr.Zero, cert.DangerousGetHandle (), key.DangerousGetHandle ());
				if (!MonoCertificatePal.IsSecIdentity (identity))
					throw new InvalidOperationException ();

				return new SafeSecIdentityHandle (identity, true);
			}
		}

		static byte[] ExportKey (RSA key)
		{
#if MONO_FEATURE_BTLS
			using (var btlsKey = MonoBtlsKey.CreateFromRSAPrivateKey (key))
				return btlsKey.GetBytes (true);
#else
			return PKCS8.PrivateKeyInfo.Encode (key);
#endif
		}

		static SafeSecKeyRefHandle ImportPrivateKey (X509Certificate2 certificate)
		{
			if (!certificate.HasPrivateKey)
				throw new NotSupportedException ();

			var keyBlob = ExportKey ((RSA)certificate.PrivateKey);
			return Interop.AppleCrypto.ImportEphemeralKey (keyBlob, true);
		}
	}
}
