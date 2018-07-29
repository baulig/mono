//
// MonoCertificatePal.Mobile.cs
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
using Internal.Cryptography;
using Internal.Cryptography.Pal;
using Mono.Net;

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

		public static ICertificatePal FromBlob (byte[] rawData, SafePasswordHandle password, X509KeyStorageFlags keyStorageFlags)
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

			if (contentType == X509ContentType.Pkcs12) {
				if ((keyStorageFlags & X509KeyStorageFlags.EphemeralKeySet) == X509KeyStorageFlags.EphemeralKeySet)
					throw new PlatformNotSupportedException (SR.Cryptography_X509_NoEphemeralPfx);
				if ((keyStorageFlags & X509KeyStorageFlags.PersistKeySet) == X509KeyStorageFlags.PersistKeySet)
					throw new PlatformNotSupportedException ("Not available on mobile.");
			} else {
				password = SafePasswordHandle.InvalidHandle;
			}

			SafeSecIdentityHandle identityHandle;
			SafeSecCertificateHandle certHandle = Interop.AppleCrypto.X509ImportCertificate (
			    rawData,
			    contentType,
			    password,
			    out identityHandle);

			if (identityHandle.IsInvalid) {
				identityHandle.Dispose ();
				return new AppleCertificatePal (certHandle);
			}

			if (contentType != X509ContentType.Pkcs12) {
				Debug.Fail ("Non-PKCS12 import produced an identity handle");

				identityHandle.Dispose ();
				certHandle.Dispose ();
				throw new CryptographicException ();
			}

			MartinTest (identityHandle);

			Debug.Assert (certHandle.IsInvalid);
			certHandle.Dispose ();
			return new AppleCertificatePal (identityHandle);
		}

		[DllImport (Interop.Libraries.AppleCryptoNative)]
		static extern int AppleNativeCrypto_MartinTest (SafeSecCertificateHandle certificate);

		[DllImport (Interop.Libraries.AppleCryptoNative)]
		static extern int AppleCryptoNative_X509GetRawData (
			SafeSecCertificateHandle cert, out SafeCFDataHandle cfDataOut, out int pOSStatus);

		static void MartinTest (SafeSecIdentityHandle identity)
		{
			Console.Error.WriteLine ("MARTIN TEST!");

			using (var certificate = GetCertificate (identity)) {
				var foundIdentity = FindIdentity (certificate, false);
				Console.Error.WriteLine ($"FOUND IDENTITY: {foundIdentity}");

				var ret = AppleCryptoNative_X509GetRawData (certificate, out var dataOut, out int status);
				Console.Error.WriteLine ($"MARTIN TEST #1: {ret} {status} {dataOut}");

				var key = Interop.AppleCrypto.X509GetPrivateKeyFromIdentity (identity);
				Console.Error.WriteLine ($"MARTIN TEST #2: {key}");

				Interop.AppleCrypto.X509CopyWithPrivateKey (certificate, key);

				Console.Error.WriteLine ($"MARTIN TEST #3");

				new AppleCertificatePal (identity);

				// AppleNativeCrypto_MartinTest (certificate);

				Console.Error.WriteLine ($"MARTIN TEST DONE");
			}
		}

		static int initialized;
		static CFString ImportExportPassphase;
		static CFString ImportItemIdentity;
		static IntPtr MatchLimitAll;
		static IntPtr MatchLimitOne;
		static IntPtr MatchLimit;
		static IntPtr SecClassKey;
		static IntPtr SecClassIdentity;
		static IntPtr SecClassCertificate;
		static IntPtr ReturnRef;
		static IntPtr MatchSearchList;

		static void Initialize ()
		{
			if (Interlocked.CompareExchange (ref initialized, 1, 0) != 0)
				return;

			var handle = CFObject.dlopen (AppleTlsContext.SecurityLibrary, 0);
			if (handle == IntPtr.Zero)
				return;

			try {
				ImportExportPassphase = CFObject.GetStringConstant (handle, "kSecImportExportPassphrase");
				ImportItemIdentity = CFObject.GetStringConstant (handle, "kSecImportItemIdentity");
				MatchLimit = CFObject.GetIntPtr (handle, "kSecMatchLimit");
				MatchLimitAll = CFObject.GetIntPtr (handle, "kSecMatchLimitAll");
				MatchLimitOne = CFObject.GetIntPtr (handle, "kSecMatchLimitOne");
				SecClassKey = CFObject.GetIntPtr (handle, "kSecClass");
				SecClassIdentity = CFObject.GetIntPtr (handle, "kSecClassIdentity");
				SecClassCertificate = CFObject.GetIntPtr (handle, "kSecClassCertificate");
				ReturnRef = CFObject.GetIntPtr (handle, "kSecReturnRef");
				MatchSearchList = CFObject.GetIntPtr (handle, "kSecMatchSearchList");
			} finally {
				CFObject.dlclose (handle);
			}
		}

		static SafeSecIdentityHandle ImportIdentity (byte[] data, string password)
		{
			if (data == null)
				throw new ArgumentNullException (nameof (data));
			if (string.IsNullOrEmpty (password)) // SecPKCS12Import() doesn't allow empty passwords.
				throw new ArgumentException (nameof (password));
			Initialize ();
			using (var pwstring = CFString.Create (password))
			using (var optionDict = CFDictionary.FromObjectAndKey (pwstring.Handle, ImportExportPassphase.Handle)) {
				var result = ImportPkcs12 (data, optionDict, out var array);
				if (result != SecStatusCode.Success)
					throw new InvalidOperationException (result.ToString ());

				return new SafeSecIdentityHandle (array [0].GetValue (ImportItemIdentity.Handle));
			}
		}

		[DllImport (AppleTlsContext.SecurityLibrary)]
		extern static SecStatusCode SecPKCS12Import (IntPtr pkcs12_data, IntPtr options, out IntPtr items);

		static SecStatusCode ImportPkcs12 (byte[] buffer, CFDictionary options, out CFDictionary[] array)
		{
			using (CFData data = CFData.FromData (buffer)) {
				return ImportPkcs12 (data, options, out array);
			}
		}

		static SecStatusCode ImportPkcs12 (CFData data, CFDictionary options, out CFDictionary[] array)
		{
			if (options == null)
				throw new ArgumentNullException (nameof (options));

			var code = SecPKCS12Import (data.Handle, options.Handle, out var handle);
			array = CFArray.ArrayFromHandle<CFDictionary> (handle, h => new CFDictionary (h, false));
			if (handle != IntPtr.Zero)
				CFObject.CFRelease (handle);
			return code;
		}

		public static SafeSecIdentityHandle ImportIdentity (X509Certificate2 certificate)
		{
			if (certificate == null)
				throw new ArgumentNullException (nameof (certificate));
			if (!certificate.HasPrivateKey)
				throw new InvalidOperationException ("Need X509Certificate2 with a private key.");

			SafeSecIdentityHandle identity;
			/*
			 * SecPSK12Import does not allow any empty passwords, so let's generate
			 * a semi-random one here.
			 */
			Initialize ();
			var password = Guid.NewGuid ().ToString ();
			var pkcs12 = certificate.Export (X509ContentType.Pfx, password);
			identity = ImportIdentity (pkcs12, password);
			return identity ?? new SafeSecIdentityHandle ();
		}

		[DllImport (AppleTlsContext.SecurityLibrary)]
		extern static SecStatusCode SecItemCopyMatching (/* CFDictionaryRef */ IntPtr query, /* CFTypeRef* */ out IntPtr result);

		public static SafeSecIdentityHandle FindIdentity (SafeSecCertificateHandle certificate, bool throwOnError = false)
		{
			if (certificate == null || certificate.IsInvalid)
				throw new ObjectDisposedException (nameof (certificate));
			var identity = FindIdentity (cert => MonoCertificatePal.Equals (certificate, cert)) ?? new SafeSecIdentityHandle ();
			if (!throwOnError || identity.IsInvalid)
				return identity;

			var subject = MonoCertificatePal.GetSubjectSummary (certificate);
			throw new InvalidOperationException ($"Could not find SecIdentity for certificate '{subject}' in keychain.");
		}

		static SafeSecIdentityHandle FindIdentity (Predicate<SafeSecCertificateHandle> filter)
		{
			Initialize ();

			/*
			 * Unfortunately, SecItemCopyMatching() does not allow any search
			 * filters when looking up an identity.
			 * 
			 * The following lookup will return all identities from the keychain -
			 * we then need need to find the right one.
			 */
			using (var query = CFMutableDictionary.Create ()) {
				query.SetValue (SecClassKey, SecClassIdentity);
				query.SetValue (CFBoolean.True.Handle, ReturnRef);
				query.SetValue (MatchLimitAll, MatchLimit);

				var status = SecItemCopyMatching (query.Handle, out var ptr);
				if (status != SecStatusCode.Success || ptr == IntPtr.Zero)
					return null;

				using (var array = new CFArray (ptr, false)) {
					for (int i = 0; i < array.Count; i++) {
						var item = array[i];
						if (!MonoCertificatePal.IsSecIdentity (item))
							throw new InvalidOperationException ();
						using (var identity = new SafeSecIdentityHandle (item))
						using (var certificate = MonoCertificatePal.GetCertificate (identity)) {
							if (filter (certificate))
								return new SafeSecIdentityHandle (item);
						}
					}
				}
			}

			return null;
		}
	}
}
