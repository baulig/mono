//
// MonoBtlsObject.cs
//
// Author:
//       Martin Baulig <martin.baulig@xamarin.com>
//
// Copyright (c) 2016 Xamarin Inc. (http://www.xamarin.com)
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
#if SECURITY_DEP && MONO_FEATURE_BTLS
using System;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Mono.Btls
{
	abstract class MonoBtlsObject : IDisposable
	{
		internal const string BTLS_DYLIB = "libmono-btls-shared";

		static long countHandles;
		static object syncRoot = new object ();
		static Dictionary<long,string> handleHash = new Dictionary<long,string> ();
		static long nextID;

		internal MonoBtlsObject (MonoBtlsHandle handle)
		{
			this.handle = handle;
		}

		protected internal abstract class MonoBtlsHandle : SafeHandle
		{
			internal MonoBtlsHandle ()
				: this (IntPtr.Zero, true)
			{
			}

			long ID;

			internal MonoBtlsHandle (IntPtr handle, bool ownsHandle)
				: base (handle, ownsHandle)
			{
				if (!ownsHandle)
					return;
				lock (syncRoot) {
					ID = ++nextID;
					var name = GetType ().Name;
					handleHash.Add (ID, name);
					Interlocked.Increment (ref countHandles);
					// Console.WriteLine ($"CREATE HANDLE: {name} {ID} {handle.ToInt64 ()}");
				}
			}

			protected override bool ReleaseHandle ()
			{
				lock (syncRoot) {
					handleHash.Remove (ID);
					// Console.WriteLine ($"RELEASE HANDLE: {GetType ().Name} {ID}");
					Interlocked.Decrement (ref countHandles);
				}
				return DoReleaseHandle ();
			}

			protected abstract bool DoReleaseHandle ();

			public override bool IsInvalid {
				get { return handle == IntPtr.Zero; }
			}
		}

		internal MonoBtlsHandle Handle {
			get {
				CheckThrow ();
				return handle;
			}
		}

		public bool IsValid {
			get { return handle != null && !handle.IsInvalid; }
		}

		MonoBtlsHandle handle;
		Exception lastError;

		protected void CheckThrow ()
		{
			if (lastError != null)
				throw lastError;
			if (handle == null || handle.IsInvalid)
				throw new ObjectDisposedException ("MonoBtlsSsl");
		}

		protected Exception SetException (Exception ex)
		{
			if (lastError == null)
				lastError = ex;
			return ex;
		}

		protected void CheckError (bool ok, [CallerMemberName] string callerName = null)
		{
			if (!ok) {
				if (callerName != null)
					throw new MonoBtlsException ("{0}.{1} failed.", GetType ().Name, callerName);
				else
					throw new MonoBtlsException ();
			}

		}

		protected void CheckError (int ret, [CallerMemberName] string callerName = null)
		{
			CheckError (ret == 1, callerName);
		}

		protected internal void CheckLastError ([CallerMemberName] string callerName = null)
		{
			var error = Interlocked.Exchange (ref lastError, null);
			if (error == null)
				return;

			string message;
			if (callerName != null)
				message = string.Format ("Caught unhandled exception in {0}.{1}.", GetType ().Name, callerName);
			else
				message = string.Format ("Caught unhandled exception.");
			throw new MonoBtlsException (message, error);
		}

		[DllImport (BTLS_DYLIB)]
		extern static void mono_btls_free (IntPtr data);

		protected void FreeDataPtr (IntPtr data)
		{
			mono_btls_free (data);
		}

		protected virtual void Close ()
		{
		}

		protected void Dispose (bool disposing)
		{
			if (disposing) {
				try {
					if (handle != null) {
						Close ();
						handle.Dispose ();
						handle = null;
					}
				} finally {
					var disposedExc = new ObjectDisposedException (GetType ().Name);
					Interlocked.CompareExchange (ref lastError, disposedExc, null);
				}
			}
		}

		internal static void MartinTest ()
		{
			lock (syncRoot) {
				Console.WriteLine ($"MonoBtlsObject: {countHandles}");
				var byType = new Dictionary<string,int> ();
				foreach (var entry in handleHash) {
					if (byType.ContainsKey (entry.Value))
						byType [entry.Value]++;
					else
						byType.Add (entry.Value, 1);
				}
				foreach (var entry in byType) {
					Console.WriteLine ($"  {entry.Key} {entry.Value}");
				}
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		~MonoBtlsObject ()
		{
			Dispose (false);
		}
	}
}
#endif
