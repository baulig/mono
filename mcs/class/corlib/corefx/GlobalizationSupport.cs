// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;

namespace System.Globalization
{
	static class GlobalizationSupport
	{
#if WASM
		public const bool SupportsManagedCollation = false;
#else
		public const bool SupportsManagedCollation = true;
#endif
	}
}
