using System;
using System.Security.Cryptography;
using AppleCrypto = Interop.AppleCrypto;

namespace Mono
{
	public static class MartinTest
	{
		public static RSA CreateRSA ()
		{
			return new RSAImplementation.RSASecurityTransforms ();
		}

		public static ECDsa CreateECDsa ()
		{
			return new ECDsaImplementation.ECDsaSecurityTransforms ();
		}

		public static bool TryRsaEncryptionPrimitive (
			RSA rsa, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
		{
			var keyPair = ((RSAImplementation.RSASecurityTransforms)rsa).GetKeys ();
			return AppleCrypto.TryRsaEncryptionPrimitive (keyPair.PublicKey, source, destination, out bytesWritten);
		}


		public static bool TryRsaDecryptionPrimitive (
			RSA rsa, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
		{
			var keyPair = ((RSAImplementation.RSASecurityTransforms)rsa).GetKeys ();
			return AppleCrypto.TryRsaDecryptionPrimitive (keyPair.PrivateKey, source, destination, out bytesWritten);
		}
	}
}