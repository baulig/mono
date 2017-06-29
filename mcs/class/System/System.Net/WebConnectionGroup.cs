//
// System.Net.WebConnectionGroup
//
// Authors:
//	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//      Martin Baulig (martin.baulig@xamarin.com)
//
// (C) 2003 Ximian, Inc (http://www.ximian.com)
// Copyright 2011-2014 Xamarin, Inc (http://www.xamarin.com)
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
#define MARTIN_DEBUG
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Net.Configuration;
using System.Net.Sockets;
using System.Diagnostics;

namespace System.Net
{
	class WebConnectionGroup
	{
		LinkedList<WebConnectionState> connections;
		Queue queue;
		bool closing;

		public ServicePoint ServicePoint {
			get;
		}

		public string Name {
			get;
		}

		public WebConnectionGroup (ServicePoint sPoint, string name)
		{
			ServicePoint = sPoint;
			Name = name;
			connections = new LinkedList<WebConnectionState> ();
			queue = new Queue ();
		}

		public event EventHandler ConnectionClosed;

		void OnConnectionClosed ()
		{
			if (ConnectionClosed != null)
				ConnectionClosed (this, null);
		}

		public void Close ()
		{
			List<WebConnection> connectionsToClose = null;

			//TODO: what do we do with the queue? Empty it out and abort the requests?
			//TODO: abort requests or wait for them to finish
			lock (ServicePoint) {
				closing = true;
				var iter = connections.First;
				while (iter != null) {
					var cnc = iter.Value.Connection;
					var node = iter;
					iter = iter.Next;

					// Closing connections inside the lock leads to a deadlock.
					if (connectionsToClose == null)
						connectionsToClose = new List<WebConnection>();

					connectionsToClose.Add (cnc);
					connections.Remove (node);
				}
			}

			if (connectionsToClose != null) {
				foreach (var cnc in connectionsToClose) {
					cnc.Close ();
					OnConnectionClosed ();
				}
			}
		}

		public (WebConnectionState, bool) SendRequest (WebOperation operation)
		{
			lock (ServicePoint) {
				var (cnc, created, started) = CreateOrReuseConnection (operation);
				WebConnection.Debug ($"WCG SEND REQUEST: Op={operation.ID} {created} {started}");

				if (started)
					ScheduleWaitForCompletion (cnc, operation);
				else {
					queue.Enqueue (operation);
					WarnAboutQueue ();
				}

				return (cnc, created);
			}
		}

#if MONOTOUCH
		static int warned_about_queue = 0;
#endif

		static void WarnAboutQueue ()
		{
#if MONOTOUCH
			if (Interlocked.CompareExchange (ref warned_about_queue, 1, 0) == 0)
				Console.WriteLine ("WARNING: An HttpWebRequest was added to the ConnectionGroup queue because the connection limit was reached.");
#endif
		}

		async void ScheduleWaitForCompletion (WebConnectionState state, WebOperation operation)
		{
			while (operation != null) {
				var (keepAlive, next) = await WaitForCompletion (state, operation).ConfigureAwait (false);
				state.Continue (keepAlive, next);
				operation = next;
			}
		}

		async Task<(bool, WebOperation)> WaitForCompletion (WebConnectionState state, WebOperation operation)
		{
			WebConnection.Debug ($"WCG WAIT FOR COMPLETION: Op={operation.ID}");

			Exception throwMe = null;
			bool keepAlive;
			try {
				keepAlive = await operation.WaitForCompletion ().ConfigureAwait (false);
			} catch (Exception ex) {
				throwMe = ex;
				keepAlive = false;
			}

			WebConnection.Debug ($"WCG WAIT FOR COMPLETION #1: Op={operation.ID} {keepAlive} {throwMe?.GetType ()}");

			WebOperation next = null;
			lock (ServicePoint) {
				if (!keepAlive)
					RemoveConnection (state);
				while (queue.Count > 0 && next == null) {
					next = (WebOperation)queue.Dequeue ();
					if (next.Aborted)
						next = null;
				}
			}

			WebConnection.Debug ($"WCG WAIT FOR COMPLETION DONE: Op={operation.ID} {keepAlive} {next?.ID}");

			return (keepAlive, next);
		}

		void RemoveConnection (WebConnectionState state)
		{
			var iter = connections.First;
			while (iter != null) {
				var current = iter.Value;
				var node = iter;
				iter = iter.Next;

				if (state == current) {
					connections.Remove (current);
					break;
				}
			}
		}

		WebConnectionState FindIdleConnection (WebOperation operation)
		{
			foreach (var cnc in connections) {
				if (!cnc.StartOperation (operation, true))
					continue;

				connections.Remove (cnc);
				connections.AddFirst (cnc);
				return cnc;
			}

			return null;
		}

		(WebConnectionState state, bool created, bool started) CreateOrReuseConnection (WebOperation operation)
		{
			var cnc = FindIdleConnection (operation);
			if (cnc != null)
				return (cnc, false, true);

			if (ServicePoint.ConnectionLimit > connections.Count || connections.Count == 0) {
				cnc = new WebConnectionState (this);
				cnc.StartOperation (operation, false);
				connections.AddFirst (cnc);
				return (cnc, true, true);
			}

			cnc = connections.Last.Value;
			connections.Remove (cnc);
			connections.AddFirst (cnc);
			return (cnc, false, false);
		}

		internal Queue Queue {
			get { return queue; }
		}

		internal bool TryRecycle (TimeSpan maxIdleTime, ref DateTime idleSince)
		{
			var now = DateTime.UtcNow;

		again:
			bool recycled;
			List<WebConnection> connectionsToClose = null;

			lock (ServicePoint) {
				if (closing) {
					idleSince = DateTime.MinValue;
					return true;
				}

				int count = 0;
				var iter = connections.First;
				while (iter != null) {
					var cnc = iter.Value;
					var node = iter;
					iter = iter.Next;

					++count;
					if (cnc.Busy)
						continue;

					if (count <= ServicePoint.ConnectionLimit && now - cnc.IdleSince < maxIdleTime) {
						if (cnc.IdleSince > idleSince)
							idleSince = cnc.IdleSince;
						continue;
					}

					/*
					 * Do not call WebConnection.Close() while holding the ServicePoint lock
					 * as this could deadlock when attempting to take the WebConnection lock.
					 * 
					 */

					if (connectionsToClose == null)
						connectionsToClose = new List<WebConnection> ();
					connectionsToClose.Add (cnc.Connection);
					connections.Remove (node);
				}

				recycled = connections.Count == 0;
			}

			// Did we find anything that can be closed?
			if (connectionsToClose == null)
				return recycled;

			// Ok, let's get rid of these!
			foreach (var cnc in connectionsToClose)
				cnc.Close ();

			// Re-take the lock, then remove them from the connection list.
			goto again;
		}
	}
}

