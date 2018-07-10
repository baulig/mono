using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Apple;
#if !MONOTOUCH
using AppleCrypto = global::Interop.AppleCrypto;
#endif

using Microsoft.Win32.SafeHandles;

namespace Mono.AppleTls
{
	internal static class MartinTest
	{
		internal static void Run ()
		{
			Import ();
		}

		internal static SafeSecCertificateHandle Import ()
		{
#if !MONOTOUCH
			var bytes = File.ReadAllBytes ("/Workspace/web-tests/CA/Hamiller-Tube-CA.pem");

			var password = SafePasswordHandle.InvalidHandle;
			var keychain = SafeTemporaryKeychainHandle.InvalidHandle;

			var certificate = AppleCrypto.X509ImportCertificate (
				bytes, X509ContentType.Cert, password, keychain, false, out var identity);

			Console.Error.WriteLine ($"IMPORT: {certificate} {identity}");

			return certificate;
#else
			return null;
#endif
		}
	}
}
