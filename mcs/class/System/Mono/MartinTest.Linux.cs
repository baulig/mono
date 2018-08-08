using System;
using System.Security.Cryptography;

namespace Mono
{
	public static class MartinTest
	{
		public static RSA CreateRSA ()
		{
			return new RSAOpenSsl ();
		}

		public static ECDsa CreateECDsa ()
		{
			return new ECDsaOpenSsl ();
		}
	}
}
