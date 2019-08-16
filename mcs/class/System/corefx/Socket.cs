using System.Threading;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System.Net.Sockets
{
    partial class Socket
    {
#region CoreFX Code

        // These flags monitor if the socket was ever connected at any time and if it still is.
        private bool _isConnected;
        private bool _isDisconnected;

        // When the socket is created it will be in blocking mode. We'll only be able to Accept or Connect,
        // so we need to handle one of these cases at a time.
        private bool _willBlock = true; // Desired state of the socket from the user.
        private bool _willBlockInternal = true; // Actual win32 state of the socket.
        private bool _isListening = false;

        // Our internal state doesn't automatically get updated after a non-blocking connect
        // completes.  Keep track of whether we're doing a non-blocking connect, and make sure
        // to poll for the real state until we're done connecting.
        private bool _nonBlockingConnectInProgress;

        // Keep track of the kind of endpoint used to do a non-blocking connect, so we can set
        // it to _rightEndPoint when we discover we're connected.
        private EndPoint _nonBlockingConnectRightEndPoint;

        // These are constants initialized by constructor.
        private AddressFamily _addressFamily;
        private SocketType _socketType;
        private ProtocolType _protocolType;

        // These caches are one degree off of Socket since they're not used in the sync case/when disabled in config.
        private CacheSet _caches;

        private class CacheSet
        {
            internal CallbackClosure ConnectClosureCache;
            internal CallbackClosure AcceptClosureCache;
            internal CallbackClosure SendClosureCache;
            internal CallbackClosure ReceiveClosureCache;
        }

#endregion

        void ValidateForMultiConnect(bool isMultiEndpoint)
        {
            // ValidateForMultiConnect is called before any {Begin}Connect{Async} call,
            // regardless of whether it's targeting an endpoint with multiple addresses.
            // If it is targeting such an endpoint, then any exposure of the socket's handle
            // or configuration of the socket we haven't tracked would prevent us from
            // replicating the socket's file descriptor appropriately.  Similarly, if it's
            // only targeting a single address, but it already experienced a failure in a
            // previous connect call, then this is logically part of a multi endpoint connect,
            // and the same logic applies.  Either way, in such a situation we throw.
            if (_handle.ExposedHandleOrUntrackedConfiguration && (isMultiEndpoint || _handle.LastConnectFailed))
            {
                ThrowMultiConnectNotSupported();
            }

            // If the socket was already used for a failed connect attempt, replace it
            // with a fresh one, copying over all of the state we've tracked.
            ReplaceHandleIfNecessaryAfterFailedConnect();
            Debug.Assert(!_handle.LastConnectFailed);
        }

        internal void ReplaceHandleIfNecessaryAfterFailedConnect()
        {
            if (!_handle.LastConnectFailed)
            {
                return;
            }

            SocketError errorCode = ReplaceHandle();
            if (errorCode != SocketError.Success)
            {
                throw new SocketException((int) errorCode);
            }

            _handle.LastConnectFailed = false;
        }

        internal SocketError ReplaceHandle()
        {
            // Copy out values from key options. The copied values should be kept in sync with the
            // handling in SafeCloseSocket.TrackOption.  Note that we copy these values out first, before
            // we change _handle, so that we can use the helpers on Socket which internally access _handle.
            // Then once _handle is switched to the new one, we can call the setters to propagate the retrieved
            // values back out to the new underlying socket.
            bool broadcast = false, dontFragment = false, noDelay = false;
            int receiveSize = -1, receiveTimeout = -1, sendSize = -1, sendTimeout = -1;
            short ttl = -1;
            LingerOption linger = null;
            if (_handle.IsTrackedOption(TrackedSocketOptions.DontFragment)) dontFragment = DontFragment;
            if (_handle.IsTrackedOption(TrackedSocketOptions.EnableBroadcast)) broadcast = EnableBroadcast;
            if (_handle.IsTrackedOption(TrackedSocketOptions.LingerState)) linger = LingerState;
            if (_handle.IsTrackedOption(TrackedSocketOptions.NoDelay)) noDelay = NoDelay;
            if (_handle.IsTrackedOption(TrackedSocketOptions.ReceiveBufferSize)) receiveSize = ReceiveBufferSize;
            if (_handle.IsTrackedOption(TrackedSocketOptions.ReceiveTimeout)) receiveTimeout = ReceiveTimeout;
            if (_handle.IsTrackedOption(TrackedSocketOptions.SendBufferSize)) sendSize = SendBufferSize;
            if (_handle.IsTrackedOption(TrackedSocketOptions.SendTimeout)) sendTimeout = SendTimeout;
            if (_handle.IsTrackedOption(TrackedSocketOptions.Ttl)) ttl = Ttl;

            // Then replace the handle with a new one
            SafeCloseSocket oldHandle = _handle;
            SocketError errorCode = SocketPal.CreateSocket(_addressFamily, _socketType, _protocolType, out _handle);
            oldHandle.TransferTrackedState(_handle);
            oldHandle.Dispose();
            if (errorCode != SocketError.Success)
            {
                return errorCode;
            }

            // And put back the copied settings.  For DualMode, we use the value stored in the _handle
            // rather than querying the socket itself, as on Unix stacks binding a dual-mode socket to
            // an IPv6 address may cause the IPv6Only setting to revert to true.
            if (_handle.IsTrackedOption(TrackedSocketOptions.DualMode)) DualMode = _handle.DualMode;
            if (_handle.IsTrackedOption(TrackedSocketOptions.DontFragment)) DontFragment = dontFragment;
            if (_handle.IsTrackedOption(TrackedSocketOptions.EnableBroadcast)) EnableBroadcast = broadcast;
            if (_handle.IsTrackedOption(TrackedSocketOptions.LingerState)) LingerState = linger;
            if (_handle.IsTrackedOption(TrackedSocketOptions.NoDelay)) NoDelay = noDelay;
            if (_handle.IsTrackedOption(TrackedSocketOptions.ReceiveBufferSize)) ReceiveBufferSize = receiveSize;
            if (_handle.IsTrackedOption(TrackedSocketOptions.ReceiveTimeout)) ReceiveTimeout = receiveTimeout;
            if (_handle.IsTrackedOption(TrackedSocketOptions.SendBufferSize)) SendBufferSize = sendSize;
            if (_handle.IsTrackedOption(TrackedSocketOptions.SendTimeout)) SendTimeout = sendTimeout;
            if (_handle.IsTrackedOption(TrackedSocketOptions.Ttl)) Ttl = ttl;

            return SocketError.Success;
        }

        private static void ThrowMultiConnectNotSupported()
        {
            throw new PlatformNotSupportedException(SR.net_sockets_connect_multiconnect_notsupported);
        }

#region CoreFX Code

        internal void SetToConnected()
        {
            if (_isConnected)
            {
                // Socket was already connected.
                return;
            }

            // Update the status: this socket was indeed connected at
            // some point in time update the perf counter as well.
            _isConnected = true;
            _isDisconnected = false;
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, "now connected");
        }

        internal void SetToDisconnected()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            if (!_isConnected)
            {
                // Socket was already disconnected.
                return;
            }

            // Update the status: this socket was indeed disconnected at
            // some point in time, clear any async select bits.
            _isConnected = false;
            _isDisconnected = true;

            if (!CleanedUp)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, "!CleanedUp");
            }
        }

#endregion

        private void UpdateStatusAfterSocketErrorAndThrowException(SocketError error, [CallerMemberName] string callerName = null)
        {
            // Update the internal state of this socket according to the error before throwing.
            var socketException = new SocketException((int)error);
            UpdateStatusAfterSocketError(socketException);
            if (NetEventSource.IsEnabled) NetEventSource.Error(this, socketException, memberName: callerName);
            throw socketException;
        }

        // UpdateStatusAfterSocketError(socketException) - updates the status of a connected socket
        // on which a failure occurred. it'll go to winsock and check if the connection
        // is still open and if it needs to update our internal state.
        internal void UpdateStatusAfterSocketError(SocketException socketException)
        {
            UpdateStatusAfterSocketError(socketException.SocketErrorCode);
        }

        internal void UpdateStatusAfterSocketError(SocketError errorCode)
        {
            // If we already know the socket is disconnected
            // we don't need to do anything else.
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);
            if (NetEventSource.IsEnabled) NetEventSource.Error(this, $"errorCode:{errorCode}");

            if (_isConnected && (_handle.IsInvalid || (errorCode != SocketError.WouldBlock &&
                    errorCode != SocketError.IOPending && errorCode != SocketError.NoBufferSpaceAvailable &&
                    errorCode != SocketError.TimedOut)))
            {
                // The socket is no longer a valid socket.
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, "Invalidating socket.");
                SetToDisconnected();
            }
        }

        private bool CheckErrorAndUpdateStatus(SocketError errorCode)
        {
            if (errorCode == SocketError.Success || errorCode == SocketError.IOPending)
            {
                return true;
            }

            UpdateStatusAfterSocketError(errorCode);
            return false;
        }

        private sealed class ConnectAsyncResult : ContextAwareResult
        {
            private EndPoint _endPoint;

            internal ConnectAsyncResult(object myObject, EndPoint endPoint, object myState, AsyncCallback myCallBack) :
                base(myObject, myState, myCallBack)
            {
                _endPoint = endPoint;
            }

            internal override EndPoint RemoteEndPoint
            {
                get { return _endPoint; }
            }
        }

        private sealed class MultipleAddressConnectAsyncResult : ContextAwareResult
        {
            internal MultipleAddressConnectAsyncResult(IPAddress[] addresses, int port, Socket socket, object myState, AsyncCallback myCallBack) :
                base(socket, myState, myCallBack)
            {
                _addresses = addresses;
                _port = port;
                _socket = socket;
            }

            internal Socket _socket;   // Keep this member just to avoid all the casting.
            internal IPAddress[] _addresses;
            internal int _index;
            internal int _port;
            internal Exception _lastException;

            internal override EndPoint RemoteEndPoint
            {
                get
                {
                    if (_addresses != null && _index > 0 && _index < _addresses.Length)
                    {
                        return new IPEndPoint(_addresses[_index], _port);
                    }
                    else
                    {
                        return null;
                    }
                }
            }
        }

        private static AsyncCallback s_multipleAddressConnectCallback;
        private static AsyncCallback CachedMultipleAddressConnectCallback
        {
            get
            {
                if (s_multipleAddressConnectCallback == null)
                {
                    s_multipleAddressConnectCallback = new AsyncCallback(MultipleAddressConnectCallback);
                }
                return s_multipleAddressConnectCallback;
            }
        }

        private static object PostOneBeginConnect(MultipleAddressConnectAsyncResult context)
        {
            IPAddress currentAddressSnapshot = context._addresses[context._index];

            context._socket.ReplaceHandleIfNecessaryAfterFailedConnect();

            if (!context._socket.CanTryAddressFamily(currentAddressSnapshot.AddressFamily))
            {
                return context._lastException != null ? context._lastException : new ArgumentException(SR.net_invalidAddressList, nameof(context));
            }

            try
            {
                EndPoint endPoint = new IPEndPoint(currentAddressSnapshot, context._port);

                context._socket.SnapshotAndSerialize(ref endPoint);

                IAsyncResult connectResult = context._socket.UnsafeBeginConnect(endPoint, CachedMultipleAddressConnectCallback, context);
                if (connectResult.CompletedSynchronously)
                {
                    return connectResult;
                }
            }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            {
                return exception;
            }

            return null;
        }

        private static void MultipleAddressConnectCallback(IAsyncResult result)
        {
            if (result.CompletedSynchronously)
            {
                return;
            }

            bool invokeCallback = false;

            MultipleAddressConnectAsyncResult context = (MultipleAddressConnectAsyncResult)result.AsyncState;
            try
            {
                invokeCallback = DoMultipleAddressConnectCallback(result, context);
            }
            catch (Exception exception)
            {
                context.InvokeCallback(exception);
            }

            // Invoke the callback outside of the try block so we don't catch user Exceptions.
            if (invokeCallback)
            {
                context.InvokeCallback();
            }
        }

        // This is like a regular async callback worker, except the result can be an exception.  This is a useful pattern when
        // processing should continue whether or not an async step failed.
        private static bool DoMultipleAddressConnectCallback(object result, MultipleAddressConnectAsyncResult context)
        {
            while (result != null)
            {
                Exception ex = result as Exception;
                if (ex == null)
                {
                    try
                    {
                        context._socket.EndConnect((IAsyncResult)result);
                    }
                    catch (Exception exception)
                    {
                        ex = exception;
                    }
                }

                if (ex == null)
                {
                    // Don't invoke the callback from here, because we're probably inside
                    // a catch-all block that would eat exceptions from the callback.
                    // Instead tell our caller to invoke the callback outside of its catchall.
                    return true;
                }
                else
                {
                    if (++context._index >= context._addresses.Length)
                    {
                        ExceptionDispatchInfo.Throw(ex);
                    }

                    context._lastException = ex;
                    result = PostOneBeginConnect(context);
                }
            }

            // Don't invoke the callback at all, because we've posted another async connection attempt.
            return false;
        }

        private CacheSet Caches
        {
            get
            {
                if (_caches == null)
                {
                    // It's not too bad if extra of these are created and lost.
                    _caches = new CacheSet();
                }
                return _caches;
            }
        }
    }
}