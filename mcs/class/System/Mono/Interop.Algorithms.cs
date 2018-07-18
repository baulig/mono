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

	static partial class DSAImplementation
	{
		public sealed partial class DSASecurityTransforms : DSA
		{
			public DSASecurityTransforms ()
			{
				throw new PlatformNotSupportedException ();
			}

			internal DSASecurityTransforms (SafeSecKeyRefHandle publicKey)
			{
				throw new PlatformNotSupportedException ();
			}

			internal DSASecurityTransforms (SafeSecKeyRefHandle publicKey, SafeSecKeyRefHandle privateKey)
			{
				throw new PlatformNotSupportedException ();
			}

			public override DSAParameters ExportParameters (bool includePrivateParameters)
			{
				throw new PlatformNotSupportedException ();
			}

			public override void ImportParameters (DSAParameters parameters)
			{
				throw new PlatformNotSupportedException ();
			}

			public override byte[] CreateSignature (byte[] rgbHash)
			{
				throw new PlatformNotSupportedException ();
			}

			public override bool VerifySignature (byte[] hash, byte[] signature)
			{
				throw new PlatformNotSupportedException ();
			}

			internal SecKeyPair GetKeys ()
			{
				throw new PlatformNotSupportedException ();
			}
		}
	}

	static partial class ECDsaImplementation
	{
		public sealed partial class ECDsaSecurityTransforms : ECDsa
		{
			public ECDsaSecurityTransforms ()
			{
				throw new PlatformNotSupportedException ();
			}

			internal ECDsaSecurityTransforms (SafeSecKeyRefHandle publicKey)
			{
				throw new PlatformNotSupportedException ();
			}

			internal ECDsaSecurityTransforms (SafeSecKeyRefHandle publicKey, SafeSecKeyRefHandle privateKey)
			{
				throw new PlatformNotSupportedException ();
			}

			public override byte[] SignHash (byte[] hash)
			{
				throw new PlatformNotSupportedException ();
			}

			public override bool VerifyHash (byte[] hash, byte[] signature)
			{
				throw new PlatformNotSupportedException ();
			}

			internal SecKeyPair GetKeys ()
			{
				throw new PlatformNotSupportedException ();
			}
		}
	}

}
