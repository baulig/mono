namespace System.Security.Cryptography
{
	partial class HashAlgorithm
	{
		static public HashAlgorithm Create ()
		{
#if FULL_AOT_RUNTIME
			return new System.Security.Cryptography.SHA1CryptoServiceProvider ();
#else
			return Create ("System.Security.Cryptography.HashAlgorithm");
#endif
		}

		static public HashAlgorithm Create (String hashName)
		{
			return (HashAlgorithm)CryptoConfig.CreateFromName (hashName);
		}
	}
}
