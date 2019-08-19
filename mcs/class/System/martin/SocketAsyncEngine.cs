// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Sockets
{
    internal sealed unsafe class SocketAsyncEngine
    {
        //
        // Encapsulates a particular SocketAsyncContext object's access to a SocketAsyncEngine.  
        //
        public readonly struct Token
        {
            private readonly SocketAsyncEngine _engine;
            private readonly IntPtr _handle;
            private readonly IOSelectorJob _readJob;
            private readonly IOSelectorJob _writeJob;

            public Token(SocketAsyncContext context)
            {
                AllocateToken(context, out _engine, out _handle);
                var ioares = new MyIOAsyncResult(_engine, context, _handle);
                _readJob = new IOSelectorJob(IOOperation.Read, ReadCallback, ioares);
                _writeJob = new IOSelectorJob(IOOperation.Write, WriteCallback, ioares);
            }

            private static void ReadCallback(IOAsyncResult ioares)
            {
                Console.Error.WriteLine($"SAE READ CALLBACK");
                ((MyIOAsyncResult)ioares).CompleteRead();
            }

            private static void WriteCallback(IOAsyncResult ioares)
            {
                Console.Error.WriteLine($"SAE WRITE CALLBACK");
                ((MyIOAsyncResult)ioares).CompleteWrite();
            }

            public bool WasAllocated
            {
                get { return _engine != null; }
            }

            public void Free(SafeCloseSocket socket)
            {
                if (WasAllocated)
                {
                    _engine.FreeHandle(_handle);
                }

                IOSelector.Remove(socket.DangerousGetHandle());
            }

            public void Register(SafeCloseSocket socket)
            {
                Debug.Assert(WasAllocated, "Expected WasAllocated to be true");

                IOSelector.Add(socket.DangerousGetHandle(), _readJob);
                IOSelector.Add(socket.DangerousGetHandle(), _writeJob);
            }

            class MyIOAsyncResult : IOAsyncResult
            {
                private readonly SocketAsyncEngine _engine;
                private readonly SocketAsyncContext _context;
                private readonly IntPtr _handle;

                public MyIOAsyncResult(SocketAsyncEngine engine, SocketAsyncContext context, IntPtr handle)
                {
                    this._engine = engine;
                    this._context = context;
                    this._handle = handle;
                }

                internal void CompleteRead()
                {
                    _context.HandleEvents(Interop.Sys.SocketEvents.Read);
                }
                
                internal void CompleteWrite()
                {
                    _context.HandleEvents(Interop.Sys.SocketEvents.Write);
                }

                internal override void CompleteDisposed()
                {
                    _context.HandleEvents(Interop.Sys.SocketEvents.Close);
                }
            }
        }

        private const int EventBufferCount =
#if DEBUG
            32;
#else
            1024;
#endif

        private static readonly object s_lock = new object();

        // In debug builds, force there to be 2 engines. In release builds, use half the number of processors when
        // there are at least 6. The lower bound is to avoid using multiple engines on systems which aren't servers.
        private static readonly int EngineCount =
#if DEBUG
            2;
#else
            Environment.ProcessorCount >= 6 ? Environment.ProcessorCount / 2 : 1;
#endif
        //
        // The current engines. We replace an engine when it runs out of "handle" values.
        // Must be accessed under s_lock.
        //
        private static readonly SocketAsyncEngine[] s_currentEngines = new SocketAsyncEngine[EngineCount];
        private static int s_allocateFromEngine = 0;

        //
        // Each SocketAsyncContext is associated with a particular "handle" value, used to identify that 
        // SocketAsyncContext when events are raised.  These handle values are never reused, because we do not have
        // a way to ensure that we will never see an event for a socket/handle that has been freed.  Instead, we
        // allocate monotonically increasing handle values up to some limit; when we would exceed that limit,
        // we allocate a new SocketAsyncEngine (and thus a new event port) and start the handle values over at zero.
        // Thus we can uniquely identify a given SocketAsyncContext by the *pair* {SocketAsyncEngine, handle},
        // and avoid any issues with misidentifying the target of an event we read from the port.
        //
#if DEBUG
        //
        // In debug builds, force rollover to new SocketAsyncEngine instances so that code doesn't go untested, since
        // it's very unlikely that the "real" limits will ever be reached in test code.
        //
        private static readonly IntPtr MaxHandles = (IntPtr)(EventBufferCount * 2);
#else
        //
        // In release builds, we use *very* high limits.  No 64-bit process running on release builds should ever
        // reach the handle limit for a single event port, and even 32-bit processes should see this only very rarely.
        //
        private static readonly IntPtr MaxHandles = IntPtr.Size == 4 ? (IntPtr)int.MaxValue : (IntPtr)long.MaxValue;
#endif
        private static readonly IntPtr MinHandlesForAdditionalEngine = EngineCount == 1 ? MaxHandles : (IntPtr)32;

        //
        // Sentinel handle value to identify events from the "shutdown pipe," used to signal an event loop to stop
        // processing events.
        //
        private static readonly IntPtr ShutdownHandle = (IntPtr)(-1);

        //
        // The next handle value to be allocated for this event port.
        // Must be accessed under s_lock.
        //
        private IntPtr _nextHandle;

        //
        // Count of handles that have been allocated for this event port, but not yet freed.
        // Must be accessed under s_lock.
        // 
        private IntPtr _outstandingHandles;

        //
        // Maps handle values to SocketAsyncContext instances.
        // Must be accessed under s_lock.
        //
        private readonly Dictionary<IntPtr, SocketAsyncContext> _handleToContextMap = new Dictionary<IntPtr, SocketAsyncContext>();

        //
        // True if we've reached the handle value limit for this event port, and thus must allocate a new event port
        // on the next handle allocation.
        //
        private bool IsFull { get { return _nextHandle == MaxHandles; } }

        // True if we've don't have sufficient active sockets to allow allocating a new engine.
        private bool HasLowNumberOfSockets
        {
            get
            {
                return IntPtr.Size == 4 ? _outstandingHandles.ToInt32() < MinHandlesForAdditionalEngine.ToInt32() :
                                          _outstandingHandles.ToInt64() < MinHandlesForAdditionalEngine.ToInt64();
            }
        }

        //
        // Allocates a new {SocketAsyncEngine, handle} pair.
        //
        private static void AllocateToken(SocketAsyncContext context, out SocketAsyncEngine engine, out IntPtr handle)
        {
            lock (s_lock)
            {
                engine = s_currentEngines[s_allocateFromEngine];
                if (engine == null)
                {
                    // We minimize the number of engines on applications that have a low number of concurrent sockets.
                    for (int i = 0; i < s_allocateFromEngine; i++)
                    {
                        var previousEngine = s_currentEngines[i];
                        if (previousEngine == null || previousEngine.HasLowNumberOfSockets)
                        {
                            s_allocateFromEngine = i;
                            engine = previousEngine;
                            break;
                        }
                    }
                    if (engine == null)
                    {
                        s_currentEngines[s_allocateFromEngine] = engine = new SocketAsyncEngine();
                    }
                }

                handle = engine.AllocateHandle(context);

                if (engine.IsFull)
                {
                    // We'll need to create a new event port for the next handle.
                    s_currentEngines[s_allocateFromEngine] = null;
                }

                // Round-robin to the next engine once we have sufficient sockets on this one.
                if (!engine.HasLowNumberOfSockets)
                {
                    s_allocateFromEngine = (s_allocateFromEngine + 1) % EngineCount;
                }
            }
        }

        private IntPtr AllocateHandle(SocketAsyncContext context)
        {
            Debug.Assert(Monitor.IsEntered(s_lock), "Expected s_lock to be held");
            Debug.Assert(!IsFull, "Expected !IsFull");

            IntPtr handle = _nextHandle;
            _handleToContextMap.Add(handle, context);

            _nextHandle = IntPtr.Add(_nextHandle, 1);
            _outstandingHandles = IntPtr.Add(_outstandingHandles, 1);

            Debug.Assert(handle != ShutdownHandle, $"Expected handle != ShutdownHandle: {handle}");
            return handle;
        }

        private void FreeHandle(IntPtr handle)
        {
            Debug.Assert(handle != ShutdownHandle, $"Expected handle != ShutdownHandle: {handle}");

            bool shutdownNeeded = false;

            lock (s_lock)
            {
                if (_handleToContextMap.Remove(handle))
                {
                    _outstandingHandles = IntPtr.Subtract(_outstandingHandles, 1);
                    Debug.Assert(_outstandingHandles.ToInt64() >= 0, $"Unexpected _outstandingHandles: {_outstandingHandles}");

                    //
                    // If we've allocated all possible handles for this instance, and freed them all, then 
                    // we don't need the event loop any more, and can reclaim resources.
                    //
                    if (IsFull && _outstandingHandles == IntPtr.Zero)
                    {
                        shutdownNeeded = true;
                    }
                }
            }

            //
            // Signal shutdown outside of the lock to reduce contention.  
            //
            if (shutdownNeeded)
            {
                RequestEventLoopShutdown();
            }
        }

        private SocketAsyncContext GetContextFromHandle(IntPtr handle)
        {
            Debug.Assert(handle != ShutdownHandle, $"Expected handle != ShutdownHandle: {handle}");
            Debug.Assert(handle.ToInt64() < MaxHandles.ToInt64(), $"Unexpected values: handle={handle}, MaxHandles={MaxHandles}");
            lock (s_lock)
            {
                SocketAsyncContext context;
                _handleToContextMap.TryGetValue(handle, out context);
                return context;
            }
        }

        private void RequestEventLoopShutdown()
        {
        }

    }
}
