using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Net.Sockets
{
    sealed partial class SocketAsyncContext
    {

        #region CoreFX Code

        private abstract partial class AsyncOperation
        {
            private enum State
            {
                Waiting = 0,
                Running = 1,
                Complete = 2,
                Cancelled = 3
            }

            private int _state; // Actually AsyncOperation.State.

#if DEBUG
            private int _callbackQueued; // When non-zero, the callback has been queued.
#endif

            public readonly SocketAsyncContext AssociatedContext;
            public AsyncOperation Next;
            protected object CallbackOrEvent;
            public SocketError ErrorCode;
            public byte[] SocketAddress;
            public int SocketAddressLen;

            public ManualResetEventSlim Event
            {
                get { return CallbackOrEvent as ManualResetEventSlim; }
                set { CallbackOrEvent = value; }
            }

            public AsyncOperation(SocketAsyncContext context)
            {
                AssociatedContext = context;
                Reset();
            }

            public void Reset()
            {
                _state = (int)State.Waiting;
                Next = this;
#if DEBUG
                _callbackQueued = 0;
#endif
            }

            public bool TryComplete(SocketAsyncContext context)
            {
                TraceWithContext(context, "Enter");

                bool result = DoTryComplete(context);

                TraceWithContext(context, $"Exit, result={result}");

                return result;
            }

            public bool TrySetRunning()
            {
                State oldState = (State)Interlocked.CompareExchange(ref _state, (int)State.Running, (int)State.Waiting);
                if (oldState == State.Cancelled)
                {
                    // This operation has already been cancelled, and had its completion processed.
                    // Simply return false to indicate no further processing is needed.
                    return false;
                }

                Debug.Assert(oldState == (int)State.Waiting);
                return true;
            }

            public void SetComplete()
            {
                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                Volatile.Write(ref _state, (int)State.Complete);
            }

            public void SetWaiting()
            {
                Debug.Assert(Volatile.Read(ref _state) == (int)State.Running);

                Volatile.Write(ref _state, (int)State.Waiting);
            }

            public bool TryCancel()
            {
                Trace("Enter");

                // Try to transition from Waiting to Cancelled
                var spinWait = new SpinWait();
                bool keepWaiting = true;
                while (keepWaiting)
                {
                    int state = Interlocked.CompareExchange(ref _state, (int)State.Cancelled, (int)State.Waiting);
                    switch ((State)state)
                    {
                        case State.Running:
                            // A completion attempt is in progress. Keep busy-waiting.
                            Trace("Busy wait");
                            spinWait.SpinOnce();
                            break;

                        case State.Complete:
                            // A completion attempt succeeded. Consider this operation as having completed within the timeout.
                            Trace("Exit, previously completed");
                            return false;

                        case State.Waiting:
                            // This operation was successfully cancelled.
                            // Break out of the loop to handle the cancellation
                            keepWaiting = false;
                            break;

                        case State.Cancelled:
                            // Someone else cancelled the operation.
                            // Just return true to indicate the operation was cancelled.
                            // The previous canceller will have fired the completion, etc.
                            Trace("Exit, previously cancelled");
                            return true;
                    }
                }

                Trace("Cancelled, processing completion");

                // The operation successfully cancelled.  
                // It's our responsibility to set the error code and queue the completion.
                DoAbort();

                var @event = CallbackOrEvent as ManualResetEventSlim;
                if (@event != null)
                {
                    @event.Set();
                }
                else
                {
#if DEBUG
                    Debug.Assert(Interlocked.CompareExchange(ref _callbackQueued, 1, 0) == 0, $"Unexpected _callbackQueued: {_callbackQueued}");
#endif
                    // We've marked the operation as canceled, and so should invoke the callback, but
                    // we can't pool the object, as ProcessQueue may still have a reference to it, due to
                    // using a pattern whereby it takes the lock to grab an item, but then releases the lock
                    // to do further processing on the item that's still in the list.
                    ThreadPool.UnsafeQueueUserWorkItem(o => ((AsyncOperation)o).InvokeCallback(allowPooling: false), this);
                }

                Trace("Exit");

                // Note, we leave the operation in the OperationQueue.
                // When we get around to processing it, we'll see it's cancelled and skip it.
                return true;
            }

            public void Dispatch(WaitCallback processingCallback)
            {
                ManualResetEventSlim e = Event;
                if (e != null)
                {
                    // Sync operation.  Signal waiting thread to continue processing.
                    e.Set();
                }
                else
                {
                    // Async operation.  Process the IO on the threadpool.
                    ThreadPool.UnsafeQueueUserWorkItem(processingCallback, this);
                }
            }

            // Called when op is not in the queue yet, so can't be otherwise executing
            public void DoAbort()
            {
                Abort();
                ErrorCode = SocketError.OperationAborted;
            }

            protected abstract void Abort();

            protected abstract bool DoTryComplete(SocketAsyncContext context);

            public abstract void InvokeCallback(bool allowPooling);

            [Conditional("SOCKETASYNCCONTEXT_TRACE")]
            public void Trace(string message, [CallerMemberName] string memberName = null)
            {
                OutputTrace($"{IdOf(this)}.{memberName}: {message}");
            }

            [Conditional("SOCKETASYNCCONTEXT_TRACE")]
            public void TraceWithContext(SocketAsyncContext context, string message, [CallerMemberName] string memberName = null)
            {
                OutputTrace($"{IdOf(context)}, {IdOf(this)}.{memberName}: {message}");
            }
        }

        // These two abstract classes differentiate the operations that go in the
        // read queue vs the ones that go in the write queue.
        private abstract class ReadOperation : AsyncOperation 
        {
            public ReadOperation(SocketAsyncContext context) : base(context) { }
        }

        private abstract class WriteOperation : AsyncOperation 
        {
            public WriteOperation(SocketAsyncContext context) : base(context) { }
        }

        private abstract class SendOperation : WriteOperation
        {
            public SocketFlags Flags;
            public int BytesTransferred;
            public int Offset;
            public int Count;

            public SendOperation(SocketAsyncContext context) : base(context) { }

            protected sealed override void Abort() { }

            public Action<int, byte[], int, SocketFlags, SocketError> Callback
            {
                set => CallbackOrEvent = value;
            }

            public override void InvokeCallback(bool allowPooling) =>
                ((Action<int, byte[], int, SocketFlags, SocketError>)CallbackOrEvent)(BytesTransferred, SocketAddress, SocketAddressLen, SocketFlags.None, ErrorCode);
        }

        private sealed class ConnectOperation : WriteOperation
        {
            public ConnectOperation(SocketAsyncContext context) : base(context) { }

            public Action<SocketError> Callback
            {
                set => CallbackOrEvent = value;
            }

            protected override void Abort() { }

            protected override bool DoTryComplete(SocketAsyncContext context)
            {
                bool result = SocketPal.TryCompleteConnect(context._socket, SocketAddressLen, out ErrorCode);
                context._socket.RegisterConnectResult(ErrorCode);
                return result;
            }

            public override void InvokeCallback(bool allowPooling) =>
                ((Action<SocketError>)CallbackOrEvent)(ErrorCode);
        }

        // In debug builds, this struct guards against:
        // (1) Unexpected lock reentrancy, which should never happen
        // (2) Deadlock, by setting a reasonably large timeout
        private readonly struct LockToken : IDisposable
        {
            private readonly object _lockObject;

            public LockToken(object lockObject)
            {
                Debug.Assert(lockObject != null);

                _lockObject = lockObject;

                Debug.Assert(!Monitor.IsEntered(_lockObject));

#if DEBUG
                bool success = Monitor.TryEnter(_lockObject, 10000);
                Debug.Assert(success, "Timed out waiting for queue lock");
#else
                Monitor.Enter(_lockObject);
#endif
            }

            public void Dispose()
            {
                Debug.Assert(Monitor.IsEntered(_lockObject));
                Monitor.Exit(_lockObject);
            }
        }

        private struct OperationQueue<TOperation>
            where TOperation : AsyncOperation
        {
            // Quick overview:
            // 
            // When attempting to perform an IO operation, the caller first checks IsReady,
            // and if true, attempts to perform the operation itself.
            // If this returns EWOULDBLOCK, or if the queue was not ready, then the operation
            // is enqueued by calling StartAsyncOperation and the state becomes Waiting.
            // When an epoll notification is received, we check if the state is Waiting,
            // and if so, change the state to Processing and enqueue a workitem to the threadpool 
            // to try to perform the enqueued operations.
            // If an operation is successfully performed, we remove it from the queue,
            // enqueue another threadpool workitem to process the next item in the queue (if any),
            // and call the user's completion callback.
            // If we successfully process all enqueued operations, then the state becomes Ready;
            // otherwise, the state becomes Waiting and we wait for another epoll notification.

            private enum QueueState : byte
            {
                Ready = 0,          // Indicates that data MAY be available on the socket.
                                    // Queue must be empty.
                Waiting = 1,        // Indicates that data is definitely not available on the socket.
                                    // Queue must not be empty.
                Processing = 2,     // Indicates that a thread pool item has been scheduled (and may 
                                    // be executing) to process the IO operations in the queue.
                                    // Queue must not be empty.
                Stopped = 3,        // Indicates that the queue has been stopped because the 
                                    // socket has been closed.
                                    // Queue must be empty.
            }

            // These fields define the queue state.

            private QueueState _state;      // See above
            private int _sequenceNumber;    // This sequence number is updated when we receive an epoll notification.
                                            // It allows us to detect when a new epoll notification has arrived
                                            // since the last time we checked the state of the queue.
                                            // If this happens, we MUST retry the operation, otherwise we risk
                                            // "losing" the notification and causing the operation to pend indefinitely.
            private AsyncOperation _tail;   // Queue of pending IO operations to process when data becomes available.

            // The _queueLock is used to ensure atomic access to the queue state above.
            // The lock is only ever held briefly, to read and/or update queue state, and
            // never around any external call, e.g. OS call or user code invocation.
            private object _queueLock;

            private LockToken Lock() => new LockToken(_queueLock);

            private static readonly WaitCallback s_processingCallback =
                typeof(TOperation) == typeof(ReadOperation) ? ((op) => { var operation = ((ReadOperation)op); operation.AssociatedContext._receiveQueue.ProcessAsyncOperation(operation); }) :
                typeof(TOperation) == typeof(WriteOperation) ? ((op) => { var operation = ((WriteOperation)op); operation.AssociatedContext._sendQueue.ProcessAsyncOperation(operation); }) :
                (WaitCallback)null;

            public void Init()
            {
                Debug.Assert(_queueLock == null);
                _queueLock = new object();

                _state = QueueState.Ready;
                _sequenceNumber = 0;
            }

            // IsReady returns the current _sequenceNumber, which must be passed to StartAsyncOperation below.
            public bool IsReady(SocketAsyncContext context, out int observedSequenceNumber)
            {
                using (Lock())
                {
                    observedSequenceNumber = _sequenceNumber;
                    bool isReady = (_state == QueueState.Ready);

                    Trace(context, $"{isReady}");

                    return isReady;
                }
            }

            // Return true for pending, false for completed synchronously (including failure and abort)
            public bool StartAsyncOperation(SocketAsyncContext context, TOperation operation, int observedSequenceNumber)
            {
                Trace(context, $"Enter");

                if (!context._registered)
                {
                    context.Register();
                }

                while (true)
                {
                    bool doAbort = false;
                    using (Lock())
                    {
                        switch (_state)
                        {
                            case QueueState.Ready:
                                if (observedSequenceNumber != _sequenceNumber)
                                {
                                    // The queue has become ready again since we previously checked it.
                                    // So, we need to retry the operation before we enqueue it.
                                    Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                                    observedSequenceNumber = _sequenceNumber;
                                    break;
                                }

                                // Caller tried the operation and got an EWOULDBLOCK, so we need to transition.
                                _state = QueueState.Waiting;
                                goto case QueueState.Waiting;

                            case QueueState.Waiting:
                            case QueueState.Processing:
                                // Enqueue the operation.
                                Debug.Assert(operation.Next == operation, "Expected operation.Next == operation");

                                if (_tail != null)
                                {
                                    operation.Next = _tail.Next;
                                    _tail.Next = operation;
                                }

                                _tail = operation;

                                Trace(context, $"Leave, enqueued {IdOf(operation)}");
                                return true;

                            case QueueState.Stopped:
                                Debug.Assert(_tail == null);
                                doAbort = true;
                                break;

                            default:
                                Environment.FailFast("unexpected queue state");
                                break;
                        }
                    }

                    if (doAbort)
                    {
                        operation.DoAbort();
                        Trace(context, $"Leave, queue stopped");
                        return false;
                    }

                    // Retry the operation.
                    if (operation.TryComplete(context))
                    {
                        Trace(context, $"Leave, retry succeeded");
                        return false;
                    }
                }
            }

            // Called on the epoll thread whenever we receive an epoll notification.
            public void HandleEvent(SocketAsyncContext context)
            {
                AsyncOperation op;
                using (Lock())
                {
                    Trace(context, $"Enter");

                    switch (_state)
                    {
                        case QueueState.Ready:
                            Debug.Assert(_tail == null, "State == Ready but queue is not empty!");
                            _sequenceNumber++;
                            Trace(context, $"Exit (previously ready)");
                            return;

                        case QueueState.Waiting:
                            Debug.Assert(_tail != null, "State == Waiting but queue is empty!");
                            _state = QueueState.Processing;
                            op = _tail.Next;
                            // Break out and release lock
                            break;

                        case QueueState.Processing:
                            Debug.Assert(_tail != null, "State == Processing but queue is empty!");
                            _sequenceNumber++;
                            Trace(context, $"Exit (currently processing)");
                            return;

                        case QueueState.Stopped:
                            Debug.Assert(_tail == null);
                            Trace(context, $"Exit (stopped)");
                            return;

                        default:
                            Environment.FailFast("unexpected queue state");
                            return;
                    }
                }

                // Dispatch the op so we can try to process it.
                op.Dispatch(s_processingCallback);
            }
            
            private void ProcessAsyncOperation(TOperation op)
            {
                OperationResult result = ProcessQueuedOperation(op);

                Debug.Assert(op.Event == null, "Sync operation encountered in ProcessAsyncOperation");

                if (result == OperationResult.Completed)
                {
                    // At this point, the operation has completed and it's no longer
                    // in the queue / no one else has a reference to it.  We can invoke
                    // the callback and let it pool the object if appropriate.
                    op.InvokeCallback(allowPooling: true);
                }
            }

            public enum OperationResult
            {
                Pending = 0,
                Completed = 1,
                Cancelled = 2
            }

            public OperationResult ProcessQueuedOperation(TOperation op)
            {
                SocketAsyncContext context = op.AssociatedContext;

                int observedSequenceNumber;
                using (Lock())
                {
                    Trace(context, $"Enter");

                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        Trace(context, $"Exit (stopped)");
                        return OperationResult.Cancelled;
                    }
                    else
                    {
                        Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");
                        Debug.Assert(_tail != null, "Unexpected empty queue while processing I/O");
                        Debug.Assert(op == _tail.Next, "Operation is not at head of queue???");
                        observedSequenceNumber = _sequenceNumber;
                    }
                }

                bool wasCompleted = false;
                while (true)
                {
                    // Try to change the op state to Running.  
                    // If this fails, it means the operation was previously cancelled,
                    // and we should just remove it from the queue without further processing.
                    if (!op.TrySetRunning())
                    {
                        break;
                    }

                    // Try to perform the IO
                    if (op.TryComplete(context))
                    {
                        op.SetComplete();
                        wasCompleted = true;
                        break;
                    }

                    op.SetWaiting();

                    // Check for retry and reset queue state.

                    using (Lock())
                    {
                        if (_state == QueueState.Stopped)
                        {
                            Debug.Assert(_tail == null);
                            Trace(context, $"Exit (stopped)");
                            return OperationResult.Cancelled;
                        }
                        else
                        {
                            Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");

                            if (observedSequenceNumber != _sequenceNumber)
                            {
                                // We received another epoll notification since we previously checked it.
                                // So, we need to retry the operation.
                                Debug.Assert(observedSequenceNumber - _sequenceNumber < 10000, "Very large sequence number increase???");
                                observedSequenceNumber = _sequenceNumber;
                            }
                            else
                            {
                                _state = QueueState.Waiting;
                                Trace(context, $"Exit (received EAGAIN)");
                                return OperationResult.Pending;
                            }
                        }
                    }
                }

                // Remove the op from the queue and see if there's more to process.

                AsyncOperation nextOp = null;
                using (Lock())
                {
                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                        Trace(context, $"Exit (stopped)");
                    }
                    else
                    {
                        Debug.Assert(_state == QueueState.Processing, $"_state={_state} while processing queue!");
                        Debug.Assert(_tail.Next == op, "Queue modified while processing queue");

                        if (op == _tail)
                        {
                            // No more operations to process
                            _tail = null;
                            _state = QueueState.Ready;
                            _sequenceNumber++;
                            Trace(context, $"Exit (finished queue)");
                        }
                        else
                        {
                            // Pop current operation and advance to next
                            nextOp = _tail.Next = op.Next;
                        }
                    }
                }

                if (nextOp != null)
                {
                    nextOp.Dispatch(s_processingCallback);
                }

                return (wasCompleted ? OperationResult.Completed : OperationResult.Cancelled);
            }

            public void CancelAndContinueProcessing(TOperation op)
            {
                // Note, only sync operations use this method.
                Debug.Assert(op.Event != null);

                // Remove operation from queue.
                // Note it must be there since it can only be processed and removed by the caller.
                AsyncOperation nextOp = null;
                using (Lock())
                {
                    if (_state == QueueState.Stopped)
                    {
                        Debug.Assert(_tail == null);
                    }
                    else
                    {
                        Debug.Assert(_tail != null, "Unexpected empty queue in CancelAndContinueProcessing");

                        if (_tail.Next == op)
                        {
                            // We're the head of the queue
                            if (op == _tail)
                            {
                                // No more operations 
                                _tail = null;
                            }
                            else
                            {
                                // Pop current operation and advance to next
                                _tail.Next = op.Next;
                            }

                            // We're the first op in the queue.
                            if (_state == QueueState.Processing)
                            {
                                // The queue has already handed off execution responsibility to us.
                                // We need to dispatch to the next op.
                                if (_tail == null)
                                {
                                    _state = QueueState.Ready;
                                    _sequenceNumber++;
                                }
                                else
                                {
                                    nextOp = _tail.Next;
                                }
                            }
                            else if (_state == QueueState.Waiting)
                            {
                                if (_tail == null)
                                {
                                    _state = QueueState.Ready;
                                }
                            }
                        }
                        else
                        {
                            // We're not the head of the queue.
                            // Just find this op and remove it.
                            AsyncOperation current = _tail.Next;
                            while (current.Next != op)
                            {
                                current = current.Next;
                            }

                            if (current.Next == _tail)
                            {
                                _tail = current;
                            }
                            current.Next = current.Next.Next;
                        }
                    }
                }

                if (nextOp != null)
                {
                    nextOp.Dispatch(s_processingCallback);
                }
            }

            // Called when the socket is closed.
            public void StopAndAbort(SocketAsyncContext context)
            {
                // We should be called exactly once, by SafeCloseSocket.
                Debug.Assert(_state != QueueState.Stopped);

                using (Lock())
                {
                    Trace(context, $"Enter");

                    Debug.Assert(_state != QueueState.Stopped);

                    _state = QueueState.Stopped;

                    if (_tail != null)
                    {
                        AsyncOperation op = _tail;
                        do
                        {
                            op.TryCancel();
                            op = op.Next;
                        } while (op != _tail);
                    }

                    _tail = null;

                    Trace(context, $"Exit");
                }
            }

            [Conditional("SOCKETASYNCCONTEXT_TRACE")]
            public void Trace(SocketAsyncContext context, string message, [CallerMemberName] string memberName = null)
            {
                string queueType =
                    typeof(TOperation) == typeof(ReadOperation) ? "recv" :
                    typeof(TOperation) == typeof(WriteOperation) ? "send" :
                    "???";

                OutputTrace($"{IdOf(context)}-{queueType}.{memberName}: {message}, {_state}-{_sequenceNumber}, {((_tail == null) ? "empty" : "not empty")}");
            }
        }

        private readonly SafeCloseSocket _socket;
        private OperationQueue<ReadOperation> _receiveQueue;
        private OperationQueue<WriteOperation> _sendQueue;
//        private SocketAsyncEngine.Token _asyncEngineToken;
        private bool _registered;
        private bool _nonBlockingSet;

        private readonly object _registerLock = new object();

        public SocketAsyncContext(SafeCloseSocket socket)
        {
            _socket = socket;

            _receiveQueue.Init();
            _sendQueue.Init();
        }

        private void Register()
        {
#if MARTIN_FIXME
            Debug.Assert(_nonBlockingSet);
            lock (_registerLock)
            {
                if (!_registered)
                {
                    Debug.Assert(!_asyncEngineToken.WasAllocated);
                    var token = new SocketAsyncEngine.Token(this);

                    Interop.Error errorCode;
                    if (!token.TryRegister(_socket, out errorCode))
                    {
                        token.Free();
                        if (errorCode == Interop.Error.ENOMEM || errorCode == Interop.Error.ENOSPC)
                        {
                            throw new OutOfMemoryException();
                        }
                        else
                        {
                            throw new InternalException();
                        }
                    }

                    _asyncEngineToken = token;
                    _registered = true;

                    Trace("Registered");
                }
            }
#endif
        }

        public void Close()
        {
            // Drain queues
            _sendQueue.StopAndAbort(this);
            _receiveQueue.StopAndAbort(this);

            lock (_registerLock)
            {
#if MARTIN_FIXME
                // Freeing the token will prevent any future event delivery.  This socket will be unregistered
                // from the event port automatically by the OS when it's closed.
                _asyncEngineToken.Free();
#endif
            }
        }

        //
        // Tracing stuff
        //

        // To enabled tracing:
        // (1) Add reference to System.Console in the csproj
        // (2) #define SOCKETASYNCCONTEXT_TRACE

        [Conditional("SOCKETASYNCCONTEXT_TRACE")]
        public void Trace(string message, [CallerMemberName] string memberName = null)
        {
            OutputTrace($"{IdOf(this)}.{memberName}: {message}");
        }

        [Conditional("SOCKETASYNCCONTEXT_TRACE")]
        public static void OutputTrace(string s)
        {
            // CONSIDER: Change to NetEventSource
#if SOCKETASYNCCONTEXT_TRACE
            Console.WriteLine(s);
#endif
        }

        public static string IdOf(object o) => o == null ? "(null)" : $"{o.GetType().Name}#{o.GetHashCode():X2}";

#endregion
    }
}
