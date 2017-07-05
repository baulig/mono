//
// System.Net.WebConnectionStream
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
// (C) 2004 Novell, Inc (http://www.novell.com)
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Net.Sockets;

namespace System.Net
{
	// https://blogs.msdn.microsoft.com/pfxteam/2012/02/11/building-async-coordination-primitives-part-1-asyncmanualresetevent/
	class AsyncManualResetEvent
	{
		volatile TaskCompletionSource<bool> m_tcs = new TaskCompletionSource<bool> ();

		public Task WaitAsync () { return m_tcs.Task; }

		public bool WaitOne (int millisecondTimeout)
		{
			WebConnection.Debug ($"AMRE WAIT ONE: {millisecondTimeout}");
			return m_tcs.Task.Wait (millisecondTimeout);
		}

		public async Task<bool> WaitAsync (int millisecondTimeout)
		{
			var timeoutTask = Task.Delay (millisecondTimeout);
			var ret = await Task.WhenAny (m_tcs.Task, timeoutTask).ConfigureAwait (false);
			return ret != timeoutTask;
		}

		public void Set ()
		{
			var tcs = m_tcs;
			Task.Factory.StartNew (s => ((TaskCompletionSource<bool>)s).TrySetResult (true),
			    tcs, CancellationToken.None, TaskCreationOptions.PreferFairness, TaskScheduler.Default);
			tcs.Task.Wait ();
		}

		public void Reset ()
		{
			while (true) {
				var tcs = m_tcs;
				if (!tcs.Task.IsCompleted ||
				    Interlocked.CompareExchange (ref m_tcs, new TaskCompletionSource<bool> (), tcs) == tcs)
					return;
			}
		}

		public AsyncManualResetEvent (bool state)
		{
			if (state)
				Set ();
		}
	}

	abstract class WebConnectionStream : Stream
	{
		protected bool closed;
		bool disposed;
		object locker = new object ();
		int read_timeout;
		int write_timeout;
		internal bool IgnoreIOErrors;

		protected WebConnectionStream (WebConnection cnc, WebOperation operation, Stream stream)
		{
			Connection = cnc;
			Operation = operation;
			Request = operation.Request;
			InnerStream = stream;

			read_timeout = Request.ReadWriteTimeout;
			write_timeout = read_timeout;
		}

		internal HttpWebRequest Request {
			get;
		}

		internal WebConnection Connection {
			get;
		}

		internal WebOperation Operation {
			get;
		}

		internal ServicePoint ServicePoint => Connection.ServicePoint;

		internal Stream InnerStream {
			get;
		}

		public override bool CanTimeout {
			get { return true; }
		}

		public override int ReadTimeout {
			get {
				return read_timeout;
			}

			set {
				if (value < -1)
					throw new ArgumentOutOfRangeException ("value");
				read_timeout = value;
			}
		}

		public override int WriteTimeout {
			get {
				return write_timeout;
			}

			set {
				if (value < -1)
					throw new ArgumentOutOfRangeException ("value");
				write_timeout = value;
			}
		}

		public override int Read (byte[] buffer, int offset, int size)
		{
			try {
				return ReadAsync (buffer, offset, size, CancellationToken.None).Result;
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}

		public override IAsyncResult BeginRead (byte[] buffer, int offset, int size,
							AsyncCallback cb, object state)
		{
			var task = ReadAsync (buffer, offset, size, CancellationToken.None);
			return TaskToApm.Begin (task, cb, state);
		}

		public override int EndRead (IAsyncResult r)
		{
			try {
				return TaskToApm.End<int> (r);
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}

		public override IAsyncResult BeginWrite (byte[] buffer, int offset, int size,
							 AsyncCallback cb, object state)
		{
			var task = WriteAsync (buffer, offset, size, CancellationToken.None);
			return TaskToApm.Begin (task, cb, state);
		}

		public override void EndWrite (IAsyncResult r)
		{
			if (r == null)
				throw new ArgumentNullException ("r");

			try {
				TaskToApm.End (r);
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}

		public override void Write (byte[] buffer, int offset, int size)
		{
			try {
				WriteAsync (buffer, offset, size).Wait ();
			} catch (Exception e) {
				throw HttpWebRequest.FlattenException (e);
			}
		}

		public override void Flush ()
		{
		}

		internal void InternalClose ()
		{
			disposed = true;
		}

		protected abstract void Close_internal (ref bool disposed);

		public override void Close ()
		{
			Close_internal (ref disposed);

			disposed = true;
		}

		public override long Seek (long a, SeekOrigin b)
		{
			throw new NotSupportedException ();
		}

		public override void SetLength (long a)
		{
			throw new NotSupportedException ();
		}

		public override bool CanSeek {
			get {
				return false;
			}
		}

		public override long Position {
			get { throw new NotSupportedException (); }
			set { throw new NotSupportedException (); }
		}
	}
}

