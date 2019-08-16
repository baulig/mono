using System.Threading;
using System.Diagnostics;
using System.Net.Internals;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System.Net.Sockets
{
#if MONO
    using Internals = System.Net;
#endif

    partial class Socket
    {
#region CoreFX Code

        // _rightEndPoint is null if the socket has not been bound.  Otherwise, it is any EndPoint of the
        // correct type (IPEndPoint, etc).
        internal EndPoint _rightEndPoint;
        internal EndPoint _remoteEndPoint;

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

        // Gets the local end point.
        public EndPoint LocalEndPoint
        {
            get
            {
                if (CleanedUp)
                {
                    throw new ObjectDisposedException(this.GetType().FullName);
                }

                if (_nonBlockingConnectInProgress && Poll(0, SelectMode.SelectWrite))
                {
                    // Update the state if we've become connected after a non-blocking connect.
                    _isConnected = true;
                    _rightEndPoint = _nonBlockingConnectRightEndPoint;
                    _nonBlockingConnectInProgress = false;
                }

                if (_rightEndPoint == null)
                {
                    return null;
                }

                Internals.SocketAddress socketAddress = IPEndPointExtensions.Serialize(_rightEndPoint);

                // This may throw ObjectDisposedException.
                SocketError errorCode = SocketPal.GetSockName(
                    _handle,
                    socketAddress.Buffer,
                    ref socketAddress.InternalSize);

                if (errorCode != SocketError.Success)
                {
                    UpdateStatusAfterSocketErrorAndThrowException(errorCode);
                }

                return _rightEndPoint.Create(socketAddress);
            }
        }

        // Gets the remote end point.
        public EndPoint RemoteEndPoint
        {
            get
            {
                if (CleanedUp)
                {
                    throw new ObjectDisposedException(this.GetType().FullName);
                }

                if (_remoteEndPoint == null)
                {
                    if (_nonBlockingConnectInProgress && Poll(0, SelectMode.SelectWrite))
                    {
                        // Update the state if we've become connected after a non-blocking connect.
                        _isConnected = true;
                        _rightEndPoint = _nonBlockingConnectRightEndPoint;
                        _nonBlockingConnectInProgress = false;
                    }

                    if (_rightEndPoint == null)
                    {
                        return null;
                    }

                    Internals.SocketAddress socketAddress = IPEndPointExtensions.Serialize(_rightEndPoint);

                    // This may throw ObjectDisposedException.
                    SocketError errorCode = SocketPal.GetPeerName(
                        _handle,
                        socketAddress.Buffer,
                        ref socketAddress.InternalSize);

                    if (errorCode != SocketError.Success)
                    {
                        UpdateStatusAfterSocketErrorAndThrowException(errorCode);
                    }

                    try
                    {
                        _remoteEndPoint = _rightEndPoint.Create(socketAddress);
                    }
                    catch
                    {
                    }
                }

                return _remoteEndPoint;
            }
        }

        // Gets and sets the blocking mode of a socket.
        public bool Blocking
        {
            get
            {
                // Return the user's desired blocking behaviour (not the actual win32 state).
                return _willBlock;
            }
            set
            {
                if (CleanedUp)
                {
                    throw new ObjectDisposedException(this.GetType().FullName);
                }

                if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"value:{value} willBlock:{_willBlock} willBlockInternal:{_willBlockInternal}");

                bool current;

                SocketError errorCode = InternalSetBlocking(value, out current);

                if (errorCode != SocketError.Success)
                {
                    UpdateStatusAfterSocketErrorAndThrowException(errorCode);
                }

                // The native call succeeded, update the user's desired state.
                _willBlock = current;
            }
        }

        // Establishes a connection to a remote system.
        public void Connect(EndPoint remoteEP)
        {
            if (CleanedUp)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }

            // Validate input parameters.
            if (remoteEP == null)
            {
                throw new ArgumentNullException(nameof(remoteEP));
            }

            if (_isDisconnected)
            {
                throw new InvalidOperationException(SR.net_sockets_disconnectedConnect);
            }

            if (_isListening)
            {
                throw new InvalidOperationException(SR.net_sockets_mustnotlisten);
            }

            if (_isConnected)
            {
                throw new SocketException((int)SocketError.IsConnected);
            }

            ValidateBlockingMode();

            if (NetEventSource.IsEnabled)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"DST:{remoteEP}");
            }

            DnsEndPoint dnsEP = remoteEP as DnsEndPoint;
            if (dnsEP != null)
            {
                ValidateForMultiConnect(isMultiEndpoint: true); // needs to come before CanTryAddressFamily call

                if (dnsEP.AddressFamily != AddressFamily.Unspecified && !CanTryAddressFamily(dnsEP.AddressFamily))
                {
                    throw new NotSupportedException(SR.net_invalidversion);
                }

                Connect(dnsEP.Host, dnsEP.Port);
                return;
            }

            ValidateForMultiConnect(isMultiEndpoint: false);

            EndPoint endPointSnapshot = remoteEP;
            Internals.SocketAddress socketAddress = SnapshotAndSerialize(ref endPointSnapshot);

            if (!Blocking)
            {
                _nonBlockingConnectRightEndPoint = endPointSnapshot;
                _nonBlockingConnectInProgress = true;
            }

            DoConnect(endPointSnapshot, socketAddress);
        }

        public void Connect(IPAddress address, int port)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, address);

            if (CleanedUp)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            if (!TcpValidationHelpers.ValidatePortNumber(port))
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            if (_isConnected)
            {
                throw new SocketException((int)SocketError.IsConnected);
            }

            ValidateForMultiConnect(isMultiEndpoint: false); // needs to come before CanTryAddressFamily call

            if (!CanTryAddressFamily(address.AddressFamily))
            {
                throw new NotSupportedException(SR.net_invalidversion);
            }

            IPEndPoint remoteEP = new IPEndPoint(address, port);
            Connect(remoteEP);
            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        public void Connect(string host, int port)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, host);

            if (CleanedUp)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }
            if (!TcpValidationHelpers.ValidatePortNumber(port))
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            if (_addressFamily != AddressFamily.InterNetwork && _addressFamily != AddressFamily.InterNetworkV6)
            {
                throw new NotSupportedException(SR.net_invalidversion);
            }

            // No need to call ValidateForMultiConnect(), as the validation
            // will be handled by the delegated Connect overloads.

            IPAddress parsedAddress;
            if (IPAddress.TryParse(host, out parsedAddress))
            {
                Connect(parsedAddress, port);
            }
            else
            {
                IPAddress[] addresses = Dns.GetHostAddressesAsync(host).GetAwaiter().GetResult();
                Connect(addresses, port);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        public void Connect(IPAddress[] addresses, int port)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, addresses);

            if (CleanedUp)
            {
                throw new ObjectDisposedException(this.GetType().FullName);
            }
            if (addresses == null)
            {
                throw new ArgumentNullException(nameof(addresses));
            }
            if (addresses.Length == 0)
            {
                throw new ArgumentException(SR.net_sockets_invalid_ipaddress_length, nameof(addresses));
            }
            if (!TcpValidationHelpers.ValidatePortNumber(port))
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            if (_addressFamily != AddressFamily.InterNetwork && _addressFamily != AddressFamily.InterNetworkV6)
            {
                throw new NotSupportedException(SR.net_invalidversion);
            }

            if (_isConnected)
            {
                throw new SocketException((int)SocketError.IsConnected);
            }

            ValidateForMultiConnect(isMultiEndpoint: true); // needs to come before CanTryAddressFamily call

            ExceptionDispatchInfo lastex = null;
            foreach (IPAddress address in addresses)
            {
                if (CanTryAddressFamily(address.AddressFamily))
                {
                    try
                    {
                        Connect(new IPEndPoint(address, port));
                        lastex = null;
                        break;
                    }
                    catch (Exception ex) when (!ExceptionCheck.IsFatal(ex))
                    {
                        lastex = ExceptionDispatchInfo.Capture(ex);
                    }
                }
            }

            lastex?.Throw();

            // If we're not connected, then we didn't get a valid ipaddress in the list.
            if (!Connected)
            {
                throw new ArgumentException(SR.net_invalidAddressList, nameof(addresses));
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this);
        }

        public bool ConnectAsync(SocketAsyncEventArgs e)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, e);
            bool pending;

            if (CleanedUp)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            if (e == null)
            {
                throw new ArgumentNullException(nameof(e));
            }
            if (e.HasMultipleBuffers)
            {
                throw new ArgumentException(SR.net_multibuffernotsupported, "BufferList");
            }
            if (e.RemoteEndPoint == null)
            {
                throw new ArgumentNullException("remoteEP");
            }
            if (_isListening)
            {
                throw new InvalidOperationException(SR.net_sockets_mustnotlisten);
            }

            if (_isConnected)
            {
                throw new SocketException((int)SocketError.IsConnected);
            }

            // Prepare SocketAddress.
            EndPoint endPointSnapshot = e.RemoteEndPoint;
            DnsEndPoint dnsEP = endPointSnapshot as DnsEndPoint;

            if (dnsEP != null)
            {
                if (NetEventSource.IsEnabled) NetEventSource.ConnectedAsyncDns(this);

                ValidateForMultiConnect(isMultiEndpoint: true); // needs to come before CanTryAddressFamily call

                if (dnsEP.AddressFamily != AddressFamily.Unspecified && !CanTryAddressFamily(dnsEP.AddressFamily))
                {
                    throw new NotSupportedException(SR.net_invalidversion);
                }

                MultipleConnectAsync multipleConnectAsync = new SingleSocketMultipleConnectAsync(this, true);

                e.StartOperationCommon(this, SocketAsyncOperation.Connect);
                e.StartOperationConnect(multipleConnectAsync);

                pending = multipleConnectAsync.StartConnectAsync(e, dnsEP);
            }
            else
            {
                ValidateForMultiConnect(isMultiEndpoint: false); // needs to come before CanTryAddressFamily call

                // Throw if remote address family doesn't match socket.
                if (!CanTryAddressFamily(e.RemoteEndPoint.AddressFamily))
                {
                    throw new NotSupportedException(SR.net_invalidversion);
                }

                e._socketAddress = SnapshotAndSerialize(ref endPointSnapshot);

#if !MONO
                // Do wildcard bind if socket not bound.
                if (_rightEndPoint == null)
                {
                    if (endPointSnapshot.AddressFamily == AddressFamily.InterNetwork)
                    {
                        InternalBind(new IPEndPoint(IPAddress.Any, 0));
                    }
                    else if (endPointSnapshot.AddressFamily != AddressFamily.Unix)
                    {
                        InternalBind(new IPEndPoint(IPAddress.IPv6Any, 0));
                    }
                }
#endif

                // Save the old RightEndPoint and prep new RightEndPoint.
                EndPoint oldEndPoint = _rightEndPoint;
                if (_rightEndPoint == null)
                {
                    _rightEndPoint = endPointSnapshot;
                }

                // Prepare for the native call.
                e.StartOperationCommon(this, SocketAsyncOperation.Connect);
                e.StartOperationConnect();

                // Make the native call.
                SocketError socketError = SocketError.Success;
                try
                {
                    socketError = e.DoOperationConnect(this, _handle);
                }
                catch
                {
                    _rightEndPoint = oldEndPoint;

                    // Clear in-use flag on event args object.
                    e.Complete();
                    throw;
                }

                pending = (socketError == SocketError.IOPending);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(this, pending);
            return pending;
        }

        public static bool ConnectAsync(SocketType socketType, ProtocolType protocolType, SocketAsyncEventArgs e)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(null);
            bool pending;

            if (e == null)
            {
                throw new ArgumentNullException(nameof(e));
            }
            if (e.HasMultipleBuffers)
            {
                throw new ArgumentException(SR.net_multibuffernotsupported, "BufferList");
            }
            if (e.RemoteEndPoint == null)
            {
                throw new ArgumentNullException("remoteEP");
            }

            EndPoint endPointSnapshot = e.RemoteEndPoint;
            DnsEndPoint dnsEP = endPointSnapshot as DnsEndPoint;

            if (dnsEP != null)
            {
                Socket attemptSocket = null;
                MultipleConnectAsync multipleConnectAsync = null;
                if (dnsEP.AddressFamily == AddressFamily.Unspecified)
                {
                    // This is the only *Connect* API that fully supports multiple endpoint attempts, as it's responsible
                    // for creating each Socket instance and can create one per attempt.
                    multipleConnectAsync = new DualSocketMultipleConnectAsync(socketType, protocolType);
#pragma warning restore
                }
                else
                {
                    attemptSocket = new Socket(dnsEP.AddressFamily, socketType, protocolType);
                    multipleConnectAsync = new SingleSocketMultipleConnectAsync(attemptSocket, false);
                }

                e.StartOperationCommon(attemptSocket, SocketAsyncOperation.Connect);
                e.StartOperationConnect(multipleConnectAsync);

                pending = multipleConnectAsync.StartConnectAsync(e, dnsEP);
            }
            else
            {
                Socket attemptSocket = new Socket(endPointSnapshot.AddressFamily, socketType, protocolType);
                pending = attemptSocket.ConnectAsync(e);
            }

            if (NetEventSource.IsEnabled) NetEventSource.Exit(null, pending);
            return pending;
        }

        public static void CancelConnectAsync(SocketAsyncEventArgs e)
        {
            if (e == null)
            {
                throw new ArgumentNullException(nameof(e));
            }
            e.CancelConnectAsync();
        }

        // This method will ignore failures, but returns the win32
        // error code, and will update internal state on success.
        private SocketError InternalSetBlocking(bool desired, out bool current)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, $"desired:{desired} willBlock:{_willBlock} willBlockInternal:{_willBlockInternal}");

            if (CleanedUp)
            {
                if (NetEventSource.IsEnabled) NetEventSource.Exit(this, "ObjectDisposed");
                current = _willBlock;
                return SocketError.Success;
            }

            // Can we avoid this call if willBlockInternal is already correct?
            bool willBlock = false;
            SocketError errorCode;
            try
            {
                errorCode = SocketPal.SetBlocking(_handle, desired, out willBlock);
            }
            catch (ObjectDisposedException)
            {
                errorCode = SocketError.NotSocket;
            }

            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"Interop.Winsock.ioctlsocket returns errorCode:{errorCode}");

            // We will update only internal state but only on successfull win32 call
            // so if the native call fails, the state will remain the same.
            if (errorCode == SocketError.Success)
            {
                _willBlockInternal = willBlock;
            }

            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"errorCode:{errorCode} willBlock:{_willBlock} willBlockInternal:{_willBlockInternal}");

            current = _willBlockInternal;
            return errorCode;
        }

        // This method ignores all failures.
        internal void InternalSetBlocking(bool desired)
        {
            bool current;
            InternalSetBlocking(desired, out current);
        }

        private void DoConnect(EndPoint endPointSnapshot, Internals.SocketAddress socketAddress)
        {
            if (NetEventSource.IsEnabled) NetEventSource.Enter(this, endPointSnapshot);

            SocketError errorCode = SocketPal.Connect(_handle, socketAddress.Buffer, socketAddress.Size);
#if TRACE_VERBOSE
            if (NetEventSource.IsEnabled)
            {
                try
                {
                    if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"SRC:{LocalEndPoint} DST:{RemoteEndPoint} Interop.Winsock.WSAConnect returns errorCode:{errorCode}");
                }
                catch (ObjectDisposedException) { }
            }
#endif

            // Throw an appropriate SocketException if the native call fails.
            if (errorCode != SocketError.Success)
            {
                // Update the internal state of this socket according to the error before throwing.
                SocketException socketException = SocketExceptionFactory.CreateSocketException((int)errorCode, endPointSnapshot);
                UpdateStatusAfterSocketError(socketException);
                if (NetEventSource.IsEnabled) NetEventSource.Error(this, socketException);
                throw socketException;
            }

            if (_rightEndPoint == null)
            {
                // Save a copy of the EndPoint so we can use it for Create().
                _rightEndPoint = endPointSnapshot;
            }

            if (NetEventSource.IsEnabled) NetEventSource.Info(this, $"connection to:{endPointSnapshot}");

            // Update state and performance counters.
            SetToConnected();
            if (NetEventSource.IsEnabled)
            {
                NetEventSource.Connected(this, LocalEndPoint, RemoteEndPoint);
                NetEventSource.Exit(this);
            }
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