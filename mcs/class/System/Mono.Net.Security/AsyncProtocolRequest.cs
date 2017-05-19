// #if SECURITY_DEP
//
// AsyncProtocolRequest.cs
//
// Author:
//       Martin Baulig <martin.baulig@xamarin.com>
//
// Copyright (c) 2015 Xamarin, Inc.
//
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using SD = System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Mono.Net.Security
{
	class BufferOffsetSize
	{
		public byte[] Buffer;
		public int Offset;
		public int Size;
		public int TotalBytes;
		public bool Complete;

		public int EndOffset {
			get { return Offset + Size; }
		}

		public int Remaining {
			get { return Buffer.Length - Offset - Size; }
		}

		public BufferOffsetSize (byte[] buffer, int offset, int size)
		{
			if (buffer == null)
				throw new ArgumentNullException (nameof (buffer));
			if (offset < 0)
				throw new ArgumentOutOfRangeException (nameof (offset));
			if (size < 0 || offset + size > buffer.Length)
				throw new ArgumentOutOfRangeException (nameof (size));

			Buffer = buffer;
			Offset = offset;
			Size = size;
			Complete = false;
		}

		public override string ToString ()
		{
			return string.Format ("[BufferOffsetSize: {0} {1}]", Offset, Size);
		}
	}

	class BufferOffsetSize2 : BufferOffsetSize
	{
		public readonly int InitialSize;

		public BufferOffsetSize2 (int size)
			: base (new byte[size], 0, 0)
		{
			InitialSize = size;
		}

		public void Reset ()
		{
			Offset = Size = 0;
			TotalBytes = 0;
			Buffer = new byte[InitialSize];
			Complete = false;
		}

		public void MakeRoom (int size)
		{
			if (Remaining >= size)
				return;

			int missing = size - Remaining;
			if (Offset == 0 && Size == 0) {
				Buffer = new byte[size];
				return;
			}

			var buffer = new byte[Buffer.Length + missing];
			Buffer.CopyTo (buffer, 0);
			Buffer = buffer;
		}

		public void AppendData (byte[] buffer, int offset, int size)
		{
			MakeRoom (size);
			System.Buffer.BlockCopy (buffer, offset, Buffer, EndOffset, size);
			Size += size;
		}
	}

	enum AsyncOperationStatus
	{
		NotStarted,
		Initialize,
		Continue,
		Running,
		Complete,
		WantRead,
		WantWrite,
		WantReadAndWrite,
		ReadDone,
		FinishWrite
	}

	abstract class AsyncProtocolRequest
	{
		public MobileAuthenticatedStream Parent {
			get;
		}

		public LazyAsyncResult UserAsyncResult {
			get;
		}

		public int ID => ++next_id;

		public string Name => GetType ().Name;

		public int UserResult {
			get;
			protected set;
		}

		int RequestedSize;
		int Status;
		int WriteRequested;
		TaskCompletionSource<int> tcs;
		readonly object locker = new object ();

		static int next_id;

		public AsyncProtocolRequest (MobileAuthenticatedStream parent, LazyAsyncResult lazyResult)
		{
			Parent = parent;
			UserAsyncResult = lazyResult;
		}

		public bool CompleteWithError (Exception ex)
		{
			Status = (int)AsyncOperationStatus.Complete;
			if (UserAsyncResult == null)
				return true;
			if (!UserAsyncResult.InternalPeekCompleted)
				UserAsyncResult.InvokeCallback (ex);
			return false;
		}

		[SD.Conditional ("MARTIN_DEBUG")]
		protected void Debug (string message, params object[] args)
		{
			Parent.Debug ("{0}({1}:{2}): {3}", Name, Parent.ID, ID, string.Format (message, args));
		}

		internal void RequestRead (int size)
		{
			lock (locker) {
				RequestedSize += size;
				Debug ("RequestRead: {0}", size);
			}
		}

		protected void ResetRead ()
		{
			var oldStatus = (AsyncOperationStatus)Interlocked.CompareExchange (ref Status, (int)AsyncOperationStatus.Complete, (int)AsyncOperationStatus.WantRead);
			Debug ("ResetRead: {0} {1}", oldStatus, Status);
		}

		internal void RequestWrite ()
		{
			WriteRequested = 1;
		}

		internal Task<int> StartOperation ()
		{
			Debug ("Start Operation: {0} {1}", this, Status);
			if (Interlocked.CompareExchange (ref tcs, new TaskCompletionSource<int> (), null) != null)
				throw new InvalidOperationException ();
			if (Interlocked.CompareExchange (ref Status, (int)AsyncOperationStatus.Initialize, (int)AsyncOperationStatus.NotStarted) != (int)AsyncOperationStatus.NotStarted)
				throw new InvalidOperationException ();

			ThreadPool.QueueUserWorkItem (_ => StartOperation_internal ());

			return tcs.Task;
		}

		void StartOperation_internal ()
		{
			try {
				NewProcessOperation ();
				if (UserAsyncResult != null && !UserAsyncResult.InternalPeekCompleted)
					UserAsyncResult.InvokeCallback (UserResult);
				tcs.SetResult (UserResult);
			} catch (Exception ex) {
				tcs.TrySetException (ex);
				if (UserAsyncResult == null)
					throw;
				if (!UserAsyncResult.InternalPeekCompleted)
					UserAsyncResult.InvokeCallback (ex);
			}
		}

		void ProcessOperation ()
		{
			AsyncOperationStatus status;
			do {
				status = (AsyncOperationStatus)Interlocked.Exchange (ref Status, (int)AsyncOperationStatus.Running);
				var oldStatus = status;

				Debug ("ProcessOperation: {0}", status);

				status = ProcessOperation (status);

				Debug ("ProcessOperation done: {0} -> {1}", oldStatus, status);

				if (Interlocked.Exchange (ref WriteRequested, 0) != 0) {
					// We are done, but still need to flush the write queue.
					Parent.InnerWrite ();
				}

				lock (locker) {
					if (status == AsyncOperationStatus.Complete) {
						if (RequestedSize != 0)
							throw new InvalidOperationException ();
					}

					Status = (int)status;
				}
			} while (status != AsyncOperationStatus.Complete);
		}

		void NewProcessOperation ()
		{
			var status = AsyncOperationStatus.Initialize;
			while (status != AsyncOperationStatus.Complete) {

				Debug ("ProcessOperation: {0}", status);

				if (!InnerRead ()) {
					// FIXME: error
					return;
				}

				Debug ("ProcessOperation run: {0}", status);

				AsyncOperationStatus newStatus;
				switch (status) {
				case AsyncOperationStatus.Initialize:
				case AsyncOperationStatus.Continue:
					newStatus = Run (status);
					break;
				case AsyncOperationStatus.WantRead:
				case AsyncOperationStatus.WantWrite:
					newStatus = AsyncOperationStatus.Continue;
					break;
				default:
					throw new InvalidOperationException ();
				}

				if (Interlocked.Exchange (ref WriteRequested, 0) != 0) {
					// Flush the write queue.
					Parent.InnerWrite ();
				}

				Debug ("ProcessOperation done: {0} -> {1}", status, newStatus);

				status = newStatus;
			}
		}

		bool InnerRead ()
		{
			var requestedSize = Interlocked.Exchange (ref RequestedSize, 0);
			while (requestedSize > 0) {
				Debug ("ProcessOperation - read inner: {0}", requestedSize);

				var ret = Parent.InnerRead (requestedSize);
				Debug ("ProcessOperation - read inner done: {0} - {1}", requestedSize, ret);

				if (ret < 0)
					return false;
				if (ret > requestedSize)
					throw new InvalidOperationException ();

				requestedSize -= ret;
				var newRequestedSize = Interlocked.Exchange (ref RequestedSize, 0);
				requestedSize += newRequestedSize;
			}

			return true;
		}

		AsyncOperationStatus ProcessOperation (AsyncOperationStatus status)
		{
			if (status == AsyncOperationStatus.WantRead) {
				if (RequestedSize < 0)
					throw new InvalidOperationException ();

				Debug ("ProcessOperation - read inner: {0}", RequestedSize);
				if (RequestedSize == 0)
					return AsyncOperationStatus.Continue;

				var ret = Parent.InnerRead (RequestedSize);
				Debug ("ProcessOperation - read inner done: {0} - {1}", RequestedSize, ret);

				if (ret < 0)
					return AsyncOperationStatus.ReadDone;

				RequestedSize -= ret;

				if (ret == 0 || RequestedSize == 0)
					return AsyncOperationStatus.Continue;
				else
					return AsyncOperationStatus.WantRead;
			} else if (status == AsyncOperationStatus.WantWrite) {
				Debug ("ProcessOperation - want write");
				Parent.InnerWrite ();
				Debug ("ProcessOperation - want write done");
				return AsyncOperationStatus.Continue;
			} else if (status == AsyncOperationStatus.Initialize || status == AsyncOperationStatus.Continue) {
				return Run (status);
			} else if (status == AsyncOperationStatus.ReadDone) {
				Debug ("ProcessOperation - read done");
				status = Run (status);
				Debug ("ProcessOperation - read done: {0}", status);
				return status;
			} else if (status == AsyncOperationStatus.FinishWrite) {
				Debug ("ProcessOperation - finish write");
				Parent.InnerWrite ();
				Debug ("ProcessOperation - finish write done");
				return AsyncOperationStatus.Complete;
			}

			throw new InvalidOperationException ();
		}

		protected abstract AsyncOperationStatus Run (AsyncOperationStatus status);

		public override string ToString ()
		{
			return string.Format ("[{0}]", Name);
		}
	}

	class AsyncHandshakeRequest : AsyncProtocolRequest
	{
		public AsyncHandshakeRequest (MobileAuthenticatedStream parent, LazyAsyncResult lazyResult)
			: base (parent, lazyResult)
		{
		}

		protected override AsyncOperationStatus Run (AsyncOperationStatus status)
		{
			return Parent.ProcessHandshake (status);
		}
	}

	abstract class AsyncReadOrWriteRequest : AsyncProtocolRequest
	{
		protected BufferOffsetSize UserBuffer {
			get;
		}

		protected int CurrentSize {
			get; set;
		}

		public AsyncReadOrWriteRequest (MobileAuthenticatedStream parent, byte[] buffer, int offset, int size, LazyAsyncResult lazyResult)
			: base (parent, lazyResult)
		{
			UserBuffer = new BufferOffsetSize (buffer, offset, size);
		}

		public override string ToString ()
		{
			return string.Format ("[{0}: {1}]", Name, UserBuffer);
		}
	}

	class AsyncReadRequest : AsyncReadOrWriteRequest
	{
		public AsyncReadRequest (MobileAuthenticatedStream parent, byte[] buffer, int offset, int size, LazyAsyncResult lazyResult)
			: base (parent, buffer, offset, size, lazyResult)
		{
		}

		protected override AsyncOperationStatus Run (AsyncOperationStatus status)
		{
			Debug ("ProcessRead - read user: {0} {1}", this, status);

			var (ret, wantMore) = Parent.ProcessRead (UserBuffer);

			Debug ("ProcessRead - read user done: {0} - {1} {2}", this, ret, wantMore);

			if (ret < 0) {
				UserResult = -1;
				return AsyncOperationStatus.Complete;
			}

			CurrentSize += ret;
			UserBuffer.Offset += ret;
			UserBuffer.Size -= ret;

			Debug ("Process Read - read user done #1: {0} - {1} {2}", this, CurrentSize, wantMore);

			if (wantMore && CurrentSize == 0)
				return AsyncOperationStatus.WantRead;

			ResetRead ();
			UserResult = CurrentSize;
			return AsyncOperationStatus.Complete;
		}
	}

	class AsyncWriteRequest : AsyncReadOrWriteRequest
	{
		public AsyncWriteRequest (MobileAuthenticatedStream parent, byte[] buffer, int offset, int size, LazyAsyncResult lazyResult)
			: base (parent, buffer, offset, size, lazyResult)
		{
		}

		protected override AsyncOperationStatus Run (AsyncOperationStatus status)
		{
			Debug ("ProcessWrite - write user: {0} {1}", this, status);

			if (UserBuffer.Size == 0) {
				UserResult = CurrentSize;
				return AsyncOperationStatus.Complete;
			}

			var (ret, wantMore) = Parent.ProcessWrite (UserBuffer);

			Debug ("ProcessWrite - write user done: {0} - {1} {2}", this, ret, wantMore);

			if (ret < 0) {
				UserResult = -1;
				return AsyncOperationStatus.Complete;
			}

			CurrentSize += ret;
			UserBuffer.Offset += ret;
			UserBuffer.Size -= ret;

			if (wantMore)
				return AsyncOperationStatus.Continue;

			UserResult = CurrentSize;
			return AsyncOperationStatus.Complete;
		}
	}

	class AsyncFlushRequest : AsyncProtocolRequest
	{
		public AsyncFlushRequest (MobileAuthenticatedStream parent, LazyAsyncResult lazyResult)
			: base (parent, lazyResult)
		{
		}

		protected override AsyncOperationStatus Run (AsyncOperationStatus status)
		{
			return Parent.ProcessFlush (status);
		}
	}

	class AsyncCloseRequest : AsyncProtocolRequest
	{
		public AsyncCloseRequest (MobileAuthenticatedStream parent, LazyAsyncResult lazyResult)
			: base (parent, lazyResult)
		{
		}

		protected override AsyncOperationStatus Run (AsyncOperationStatus status)
		{
			return Parent.ProcessClose (status);
		}
	}

}
// #endif
