//
// X509PalImplUnix.cs
//
// Author:
//       Martin Baulig <mabaul@microsoft.com>
//
// Copyright (c) 2018 Xamarin, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
#if MONO_SECURITY_ALIAS
extern alias MonoSecurity;
#endif

#if MONO_SECURITY_ALIAS
using MonoSecurity::Mono.Security;
using MonoSecurity::Mono.Security.Interface;
using MonoSecurity::Mono.Security.Cryptography;
#else
using Mono.Security;
using Mono.Security.Interface;
using Mono.Security.Cryptography;
#endif

using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Internal.Cryptography;
using Internal.Cryptography.Pal;

namespace Mono
{
	abstract class X509PalImplUnix : ManagedX509ExtensionProcessor, IX509Pal
	{
		public abstract AsymmetricAlgorithm DecodePublicKey (Oid oid, byte[] encodedKeyValue, byte[] encodedParameters, ICertificatePal certificatePal);

		protected AsymmetricAlgorithm DecodePublicKey (Oid oid, byte[] encodedKeyValue, byte[] encodedParameters)
		{
			switch (oid.Value) {
			case Oids.RsaRsa:
				return DecodeRSA (encodedKeyValue);
			case Oids.DsaDsa:
				return DecodeDSA (encodedKeyValue, encodedParameters);
			}

			throw new NotSupportedException (SR.NotSupported_KeyAlgorithm);
		}

		static byte[] GetUnsignedBigInteger (byte[] integer)
		{
			if (integer[0] != 0x00)
				return integer;

			// this first byte is added so we're sure it's an unsigned integer
			// however we can't feed it into RSAParameters or DSAParameters
			int length = integer.Length - 1;
			byte[] uinteger = new byte[length];
			Buffer.BlockCopy (integer, 1, uinteger, 0, length);
			return uinteger;
		}

		static DSA DecodeDSA (byte[] rawPublicKey, byte[] rawParameters)
		{
			DSAParameters dsaParams = new DSAParameters ();
			try {
				// for DSA rawPublicKey contains 1 ASN.1 integer - Y
				ASN1 pubkey = new ASN1 (rawPublicKey);
				if (pubkey.Tag != 0x02)
					throw new CryptographicException (Locale.GetText ("Missing DSA Y integer."));
				dsaParams.Y = GetUnsignedBigInteger (pubkey.Value);

				ASN1 param = new ASN1 (rawParameters);
				if ((param == null) || (param.Tag != 0x30) || (param.Count < 3))
					throw new CryptographicException (Locale.GetText ("Missing DSA parameters."));
				if ((param[0].Tag != 0x02) || (param[1].Tag != 0x02) || (param[2].Tag != 0x02))
					throw new CryptographicException (Locale.GetText ("Invalid DSA parameters."));

				dsaParams.P = GetUnsignedBigInteger (param[0].Value);
				dsaParams.Q = GetUnsignedBigInteger (param[1].Value);
				dsaParams.G = GetUnsignedBigInteger (param[2].Value);
			} catch (Exception e) {
				string msg = Locale.GetText ("Error decoding the ASN.1 structure.");
				throw new CryptographicException (msg, e);
			}

			DSA dsa = (DSA)new DSACryptoServiceProvider (dsaParams.Y.Length << 3);
			dsa.ImportParameters (dsaParams);
			return dsa;
		}

		static RSA DecodeRSA (byte[] rawPublicKey)
		{
			RSAParameters rsaParams = new RSAParameters ();
			try {
				// for RSA rawPublicKey contains 2 ASN.1 integers
				// the modulus and the public exponent
				ASN1 pubkey = new ASN1 (rawPublicKey);
				if (pubkey.Count == 0)
					throw new CryptographicException (Locale.GetText ("Missing RSA modulus and exponent."));
				ASN1 modulus = pubkey[0];
				if ((modulus == null) || (modulus.Tag != 0x02))
					throw new CryptographicException (Locale.GetText ("Missing RSA modulus."));
				ASN1 exponent = pubkey[1];
				if (exponent.Tag != 0x02)
					throw new CryptographicException (Locale.GetText ("Missing RSA public exponent."));

				rsaParams.Modulus = GetUnsignedBigInteger (modulus.Value);
				rsaParams.Exponent = exponent.Value;
			} catch (Exception e) {
				string msg = Locale.GetText ("Error decoding the ASN.1 structure.");
				throw new CryptographicException (msg, e);
			}

			int keySize = (rsaParams.Modulus.Length << 3);
			RSA rsa = (RSA)new RSACryptoServiceProvider (keySize);
			rsa.ImportParameters (rsaParams);
			return rsa;
		}

		public string X500DistinguishedNameDecode (byte[] encodedDistinguishedName, X500DistinguishedNameFlags flag)
		{
			return X500NameEncoder.X500DistinguishedNameDecode (encodedDistinguishedName, true, flag);
		}

		public byte[] X500DistinguishedNameEncode (string distinguishedName, X500DistinguishedNameFlags flag)
		{
			return X500NameEncoder.X500DistinguishedNameEncode (distinguishedName, flag);
		}

		public string X500DistinguishedNameFormat (byte[] encodedDistinguishedName, bool multiLine)
		{
			return X500NameEncoder.X500DistinguishedNameDecode (
			    encodedDistinguishedName,
			    true,
			    multiLine ? X500DistinguishedNameFlags.UseNewLines : X500DistinguishedNameFlags.None,
			    multiLine);
		}

		private static byte[] signedData = new byte[] { 0x2a, 0x86, 0x48, 0x86, 0xf7, 0x0d, 0x01, 0x07, 0x02 };

		public X509ContentType GetCertContentType (byte[] rawData)
		{
			if ((rawData == null) || (rawData.Length == 0))
				throw new ArgumentException ("rawData");

			if (rawData[0] == 0x30) {
				// ASN.1 SEQUENCE
				try {
					ASN1 data = new ASN1 (rawData);

					// SEQUENCE / SEQUENCE / BITSTRING
					if (data.Count == 3 && data[0].Tag == 0x30 && data[1].Tag == 0x30 && data[2].Tag == 0x03)
						return X509ContentType.Cert;

					// INTEGER / SEQUENCE / SEQUENCE
					if (data.Count == 3 && data[0].Tag == 0x02 && data[1].Tag == 0x30 && data[2].Tag == 0x30)
						return X509ContentType.Pkcs12; // note: Pfx == Pkcs12

					// check for PKCS#7 (count unknown but greater than 0)
					// SEQUENCE / OID (signedData)
					if (data.Count > 0 && data[0].Tag == 0x06 && data[0].CompareValue (signedData))
						return X509ContentType.Pkcs7;

					return X509ContentType.Unknown;
				} catch (Exception) {
					return X509ContentType.Unknown;
				}
			} else {
				string pem = Encoding.ASCII.GetString (rawData);
				int start = pem.IndexOf ("-----BEGIN CERTIFICATE-----");
				if (start >= 0)
					return X509ContentType.Cert;
			}

			return X509ContentType.Unknown;
		}

		public X509ContentType GetCertContentType (string fileName)
		{
			return GetCertContentType (File.ReadAllBytes (fileName));
		}
	}
}
