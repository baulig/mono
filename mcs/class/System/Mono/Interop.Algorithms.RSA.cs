using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.Apple;
using Internal.Cryptography;

namespace System.Security.Cryptography
{
	static partial class RSAImplementation
	{
		public sealed partial class RSASecurityTransforms : RSA
		{
			public RSASecurityTransforms ()
			{
				throw new PlatformNotSupportedException ();
			}

			internal RSASecurityTransforms (SafeSecKeyRefHandle publicKey)
			{
				throw new PlatformNotSupportedException ();
			}

			internal RSASecurityTransforms (SafeSecKeyRefHandle publicKey, SafeSecKeyRefHandle privateKey)
			{
				throw new PlatformNotSupportedException ();
			}

			public override RSAParameters ExportParameters (bool includePrivateParameters)
			{
				throw new PlatformNotSupportedException ();
			}

			public override void ImportParameters (RSAParameters parameters)
			{
				throw new PlatformNotSupportedException ();
			}

			internal SecKeyPair GetKeys ()
			{
				throw new PlatformNotSupportedException ();
			}
		}
	}

	static class RsaKeyBlobHelpers
	{
		internal static void ReadSubjectPublicKeyInfo (this DerSequenceReader keyInfo, ref RSAParameters parameters)
		{
			throw new PlatformNotSupportedException ();
		}

		internal static void ReadPkcs1PublicBlob (this DerSequenceReader subjectPublicKey, ref RSAParameters parameters)
		{
			throw new PlatformNotSupportedException ();
		}
	}
}
