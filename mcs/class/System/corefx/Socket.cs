using System.Threading;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System.Net.Sockets
{
    partial class Socket
    {
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
            if (m_Handle.ExposedHandleOrUntrackedConfiguration && (isMultiEndpoint || m_Handle.LastConnectFailed))
            {
                ThrowMultiConnectNotSupported();
            }

            // If the socket was already used for a failed connect attempt, replace it
            // with a fresh one, copying over all of the state we've tracked.
            ReplaceHandleIfNecessaryAfterFailedConnect();
            Debug.Assert(!m_Handle.LastConnectFailed);
        }

        internal void ReplaceHandleIfNecessaryAfterFailedConnect()
        {
            if (!m_Handle.LastConnectFailed)
            {
                return;
            }

            SocketError errorCode = ReplaceHandle();
            if (errorCode != SocketError.Success)
            {
                throw new SocketException((int) errorCode);
            }

            m_Handle.LastConnectFailed = false;
        }

        internal SocketError ReplaceHandle()
        {
            // Copy out values from key options. The copied values should be kept in sync with the
            // handling in SafeCloseSocket.TrackOption.  Note that we copy these values out first, before
            // we change m_Handle, so that we can use the helpers on Socket which internally access m_Handle.
            // Then once m_Handle is switched to the new one, we can call the setters to propagate the retrieved
            // values back out to the new underlying socket.
            bool broadcast = false, dontFragment = false, noDelay = false;
            int receiveSize = -1, receiveTimeout = -1, sendSize = -1, sendTimeout = -1;
            short ttl = -1;
            LingerOption linger = null;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.DontFragment)) dontFragment = DontFragment;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.EnableBroadcast)) broadcast = EnableBroadcast;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.LingerState)) linger = LingerState;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.NoDelay)) noDelay = NoDelay;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.ReceiveBufferSize)) receiveSize = ReceiveBufferSize;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.ReceiveTimeout)) receiveTimeout = ReceiveTimeout;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.SendBufferSize)) sendSize = SendBufferSize;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.SendTimeout)) sendTimeout = SendTimeout;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.Ttl)) ttl = Ttl;

            // Then replace the handle with a new one
            SafeCloseSocket oldHandle = m_Handle;
            SocketError errorCode = SocketPal.CreateSocket(addressFamily, socketType, protocolType, out m_Handle);
            oldHandle.TransferTrackedState(m_Handle);
            oldHandle.Dispose();
            if (errorCode != SocketError.Success)
            {
                return errorCode;
            }

            // And put back the copied settings.  For DualMode, we use the value stored in the _handle
            // rather than querying the socket itself, as on Unix stacks binding a dual-mode socket to
            // an IPv6 address may cause the IPv6Only setting to revert to true.
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.DualMode)) DualMode = m_Handle.DualMode;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.DontFragment)) DontFragment = dontFragment;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.EnableBroadcast)) EnableBroadcast = broadcast;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.LingerState)) LingerState = linger;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.NoDelay)) NoDelay = noDelay;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.ReceiveBufferSize)) ReceiveBufferSize = receiveSize;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.ReceiveTimeout)) ReceiveTimeout = receiveTimeout;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.SendBufferSize)) SendBufferSize = sendSize;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.SendTimeout)) SendTimeout = sendTimeout;
            if (m_Handle.IsTrackedOption(TrackedSocketOptions.Ttl)) Ttl = ttl;

            return SocketError.Success;
        }

        private static void ThrowMultiConnectNotSupported()
        {
            throw new PlatformNotSupportedException(SR.net_sockets_connect_multiconnect_notsupported);
        }

        internal void SetToConnected()
        {
            if (is_connected)
            {
                // Socket was already connected.
                return;
            }

            // Update the status: this socket was indeed connected at
            // some point in time update the perf counter as well.
            is_connected = true;
            is_closed = false;
            if (NetEventSource.IsEnabled) NetEventSource.Info(this, "now connected");
        }

        internal void SetToDisconnected()
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this);

            if (!is_connected)
            {
                // Socket was already disconnected.
                return;
            }

            // Update the status: this socket was indeed disconnected at
            // some point in time, clear any async select bits.
            is_connected = false;
            is_closed = true;

            if (!CleanedUp)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, "!CleanedUp");
            }
        }

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

            if (is_connected && (m_Handle.IsInvalid || (errorCode != SocketError.WouldBlock &&
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

        // These caches are one degree off of Socket since they're not used in the sync case/when disabled in config.
        private CacheSet _caches;

        private class CacheSet
        {
            internal CallbackClosure ConnectClosureCache;
            internal CallbackClosure AcceptClosureCache;
            internal CallbackClosure SendClosureCache;
            internal CallbackClosure ReceiveClosureCache;
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