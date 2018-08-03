using System.IO;
using System.Text;
using System.Security.Util;
using System.Diagnostics.Contracts;
using Mono;

namespace System.Security.Cryptography
{
	abstract partial class RSA
	{
		new static public RSA Create ()
		{
			var provider = DependencyInjector.SystemProvider.CryptographyProvider;
			if (provider.SupportsRSA)
				return provider.CreateRSA ();

#if FULL_AOT_RUNTIME
			return new System.Security.Cryptography.RSACryptoServiceProvider ();
#else
			return Create ("System.Security.Cryptography.RSA");
#endif
		}

		public override void FromXmlString (String xmlString)
		{
			throw new PlatformNotSupportedException ();
		}

		public override String ToXmlString (bool includePrivateParameters)
		{
			throw new PlatformNotSupportedException ();
		}
	}
}
