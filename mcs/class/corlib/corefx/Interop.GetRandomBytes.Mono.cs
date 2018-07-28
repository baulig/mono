using System;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

internal partial class Interop
{
	internal static unsafe void GetRandomBytes (byte* buffer, int length)
	{
		MonoRandomNumberGenerator.GetRandomBytes (buffer, length);
	}
}
