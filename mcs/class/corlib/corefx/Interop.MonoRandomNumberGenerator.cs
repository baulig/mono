using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

internal partial class Interop
{
	internal static class MonoRandomNumberGenerator
	{
		static object _rngAccess = new object ();
		static RNGCryptoServiceProvider _rng;

		internal static unsafe void Fill (Span<byte> data)
		{
			if (data.Length > 0)
				fixed (byte* ptr = data) GetRandomBytes (ptr, data.Length);
		}

		internal static void GetRandomBytes (byte[] buffer)
		{
			lock (_rngAccess) {
				if (_rng == null)
					_rng = new RNGCryptoServiceProvider ();
				_rng.GetBytes (buffer);
			}
		}

		internal static unsafe void GetRandomBytes (byte* buffer, int length)
		{
			lock (_rngAccess) {
				if (_rng == null)
					_rng = new RNGCryptoServiceProvider ();
				_rng.GetBytes (buffer, (IntPtr)length);
			}
		}
	}
}
