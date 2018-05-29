namespace System.Threading
{
	public sealed class PreAllocatedOverlapped : IDisposable
	{
		static PreAllocatedOverlapped ()
		{
			throw new PlatformNotSupportedException ();
		}

		[CLSCompliant (false)]
		public unsafe PreAllocatedOverlapped (IOCompletionCallback callback, object state, object pinData)
		{
			throw new PlatformNotSupportedException ();
		}

		public void Dispose ()
		{
			GC.SuppressFinalize (this);
		}

		~PreAllocatedOverlapped ()
		{
			//
			// During shutdown, don't automatically clean up, because this instance may still be
			// reachable/usable by other code.
			//
			if (!Environment.HasShutdownStarted)
				Dispose ();
		}
	}
}
