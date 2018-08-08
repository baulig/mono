using Test.Cryptography;
using Xunit;

namespace System.Security.Cryptography.Rsa.Tests
{
	class MartinXTests
	{
		static void Encrypt (byte[] buffer)
		{
			
		}

		[Fact]
		public void TestEncryptionPrimitive ()
		{
			using (RSA rsa = RSAFactory.Create()) {
				rsa.ImportParameters (TestData.RSA1024Params);
			}
		}



	}
}
