using System;
using System.Security.Cryptography;
using Mono.Btls.Interface;

class X
{
	static void Main ()
	{
		var data = new byte[] { 0x41, 0x42, 0x43, 0x44 };
		var hash = IncrementalHash.CreateHash (HashAlgorithmName.SHA1);
		hash.AppendData (data);
		var result = hash.GetHashAndReset ();
		Console.WriteLine (result);

		var key = new byte[] { 0x61, 0x62, 0x63, 0x64 };
		var hmac = IncrementalHash.CreateHMAC (HashAlgorithmName.SHA1, key);
		hmac.AppendData (data);
		var result2 = hmac.GetHashAndReset ();
		Console.WriteLine (result2);

		BtlsProvider.MartinTest ();
	}
}
