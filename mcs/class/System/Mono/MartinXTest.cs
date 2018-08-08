using Test.Cryptography;
using Xunit;

namespace System.Security.Cryptography.Rsa.Tests
{
	public class MartinXTests
	{
		byte[] plainBytes = ("41424344").HexToByteArray ();
		byte[] plainBytes2 = ("00000041424344").HexToByteArray ();

		byte[] cipherBytes = (
			"088025B29062638CF0620A9BE92430217A0EB856FF6C15C096DE9557B90C9F78" +
			"002B46EE5FCDB345CAE3AFB3602E854E182ED81795EBCB90B711D1D813A7C61E" +
			"7AC4D532741B18B502BE8856C297187D61FD74E9B1269EC7001E1953D377635F" +
			"6FA3D5B769587DF8D9B6306EA8B526BDBB304ADE12DABE386B73E8F0D3E8BED6").HexToByteArray();

		byte[] hashBytes = (
			"49EC55BD83FCD67838E3D385CE831669E3F815A7F44B7AA5F8D52B5D42354C46" +
			"D89C8B9D06E47A797AE4FBD22291BE15BCC35B07735C4A6F92357F93D5A33D9B").HexToByteArray();

		byte[] signatureBytes = (
			"7F41A4E872E6A47E799BD3577C798A4BB42D412372B8C2F2FF85BD58FE795DC0" +
			"6E7BAF07CE01F519350DA168FB0FC97A1F2882A0DCBBDC120185B4D2CE522717" +
			"852CC1D3F0C4B360E2D740AAF092EB0F4649254C0D67EA5735CCB6DEABD4B4D9" +
			"0CBE45C86B1F435474DBB58B7FC97F6A0BF89C7F5292467A5817F22D1EFB0878").HexToByteArray ();

		static byte[] Encrypt (RSA rsa, byte[] buffer)
		{
			Console.Error.WriteLine ($"ENCRYPT: {buffer.ByteArrayToHex ()}");
			var output = new byte [rsa.KeySize];
			var result = Mono.MartinTest.TryRsaEncryptionPrimitive (
				rsa, buffer, output, out var bytesWritten);
			Console.Error.WriteLine ($"ENCRYPT #1: {result} {bytesWritten}");

			Assert.True (result);

			var retval = new byte [bytesWritten];
			Buffer.BlockCopy (output, 0, retval, 0, bytesWritten);

			Console.Error.WriteLine ($"ENCRYPT #2: {retval.ByteArrayToHex ()}");

			return retval;
		}

		static byte[] Decrypt (RSA rsa, byte[] buffer)
		{
			Console.Error.WriteLine ($"DECRYPT: {buffer.ByteArrayToHex ()}");
			var output = new byte [rsa.KeySize];
			var result = Mono.MartinTest.TryRsaDecryptionPrimitive (
				rsa, buffer, output, out var bytesWritten);
			Console.Error.WriteLine ($"DECRYPT #1: {result} {bytesWritten}");

			Assert.True (result);

			var retval = new byte [bytesWritten];
			Buffer.BlockCopy (output, 0, retval, 0, bytesWritten);

			Console.Error.WriteLine ($"DECRYPT #2: {retval.ByteArrayToHex ()}");

			return retval;
		}

		static byte[] Sign (RSA rsa, byte[] buffer)
		{
			Console.Error.WriteLine ($"SIGN: {buffer.ByteArrayToHex ()}");

			var output = new byte [rsa.KeySize * 4];
			var result = Mono.MartinTest.TryRsaSignaturePrimitive (
				rsa, buffer, output, out var bytesWritten);
			Console.Error.WriteLine ($"SIGN #1: {result} {bytesWritten}");

			Assert.True (result);

			var retval = new byte [bytesWritten];
			Buffer.BlockCopy (output, 0, retval, 0, bytesWritten);

			Console.Error.WriteLine ($"SIGN #2: {retval.ByteArrayToHex ()}");

			return retval;
		}

		static bool Verify (RSA rsa, byte[] buffer, byte[] signature)
		{
			Console.Error.WriteLine ($"VERIFY: {buffer.ByteArrayToHex ()} - {signature.ByteArrayToHex ()}");

			var output = new byte [rsa.KeySize * 4];
			var result = Mono.MartinTest.TryRsaVerificationPrimitive (
				rsa, buffer, output, out var bytesWritten);
			Console.Error.WriteLine ($"VERIFY #1: {result} {bytesWritten}");

			Assert.True (result);

			return result;
		}

		[Fact]
		public void TestEncryptionPrimitive ()
		{
			using (RSA rsa = RSAFactory.Create()) {
				rsa.ImportParameters (TestData.RSA1024Params);

				var output = Encrypt (rsa, plainBytes);
				Assert.Equal (cipherBytes, output);
			}
		}

		[Fact]
		public void TestDecryptionPrimitive ()
		{
			using (RSA rsa = RSAFactory.Create()) {
				rsa.ImportParameters (TestData.RSA1024Params);

				var output = Decrypt (rsa, cipherBytes);

				var plain = new byte [plainBytes.Length];
				Buffer.BlockCopy (output, output.Length - plain.Length, plain, 0, plain.Length);
				Assert.Equal (plainBytes, plain);
			}
		}

		[Fact]
		public void TestEncryptionPrimitive2 ()
		{
			using (RSA rsa = RSAFactory.Create()) {
				rsa.ImportParameters (TestData.RSA1024Params);

				var output = Encrypt (rsa, plainBytes2);
				Assert.Equal (cipherBytes, output);
			}
		}

		[Fact]
		public void TestSignaturePrimitive ()
		{
			using (RSA rsa = RSAFactory.Create()) {
				rsa.ImportParameters (TestData.RSA1024Params);

				var sha = SHA512.Create ();
				var hash = sha.ComputeHash (plainBytes);
				Assert.Equal (hashBytes, hash);

				var output = Sign (rsa, hash);
				Assert.Equal (signatureBytes, output);
			}
		}

		[Fact]
		public void TestVerificationPrimitive ()
		{
			using (RSA rsa = RSAFactory.Create()) {
				rsa.ImportParameters (TestData.RSA1024Params);

				var result = Verify (rsa, hashBytes, signatureBytes);
				Assert.True (result);
			}
		}
	
	}
}
