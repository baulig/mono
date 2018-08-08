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
		byte[] decryptedBytes = (
			"0000000000000000000000000000000000000000000000000000000000000000" +
			"0000000000000000000000000000000000000000000000000000000000000000" +
			"0000000000000000000000000000000000000000000000000000000000000000" +
			"0000000000000000000000000000000000000000000000000000000041424344").HexToByteArray();

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
	}
}
