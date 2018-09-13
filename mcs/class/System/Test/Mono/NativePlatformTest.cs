using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace MonoTests.Mono
{
	[TestFixture]
	public class NativePlatformTest
	{
		[DllImport ("System.Native")]
		extern static int mono_native_get_platform_type ();

		[TestFixtureSetUp]
		public void SetUp ()
		{
			if (!NativePlatformType.IsSupported)
				Assert.Ignore ("Mono.Native is not supported on this platform.");
		}

		[Test]
		public void Test ()
		{
			var type = mono_native_get_platform_type ();
			Console.Error.WriteLine ($"NATIVE PLATFORM TYPE: {type}");
			Assert.That (type, Is.GreaterThan (1), "platform type");

			var usingCompat = (type & 16384) != 0;
			Assert.AreEqual (usingCompat, NativePlatformType.UsingCompat, "using compatibility layer");
		}
	}
}
