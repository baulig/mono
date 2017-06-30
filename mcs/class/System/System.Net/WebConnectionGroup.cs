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
		LinkedList<WebConnection> connections;
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
			connections = new LinkedList<WebConnection> ();
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
					var cnc = iter.Value;
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

		public bool SendRequest (WebOperation operation)
		{
			lock (ServicePoint) {
				var (cnc, created) = CreateOrReuseConnection (operation, false);
				WebConnection.Debug ($"WCG SEND REQUEST: Op={operation.ID} {cnc != null} {created}");

				if (cnc != null) {
					ScheduleWaitForCompletion (cnc, operation);
				} else {
					queue.Enqueue (operation);
					WarnAboutQueue ();
				}

				return created;
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

		async void ScheduleWaitForCompletion (WebConnection connection, WebOperation operation)
		{
			while (operation != null) {
				var (keepAlive, next) = await WaitForCompletion (connection, operation).ConfigureAwait (false);

				lock (ServicePoint) {
					var started = connection.Continue (ref keepAlive, false, next);

					WebConnection.Debug ($"WCG WAIT FOR COMPLETION LOOP: Cnc={connection.ID} Op={operation.ID} started={started} keepalive={keepAlive} next={next?.ID}");

					if (started) {
						// WaitForCompletion() found another request in the queue and
						// WebConnection.Continue() has accepted to be reused.
						operation = next;
						continue;
					}

					if (!keepAlive) {
						// Get rid of the connection unless it has accepted to run another
						// request.
						RemoveConnection (connection);
					}

					if (next == null) {
						// We are done.
						return;
					}

					/*
					 * At this point, `keepAlive' is always false because `connection.Continue()'
					 * would have returned true otherwise.
					 * 
					 * Let's try to find another idle connection or force creating a new one.
					 */
					var (newCnc, created) = CreateOrReuseConnection (next, true);
					connection = newCnc;
					operation = next;
				}
			}
		}

		async Task<(bool, WebOperation)> WaitForCompletion (WebConnection connection, WebOperation operation)
		{
			WebConnection.Debug ($"WCG WAIT FOR COMPLETION: Cnc={connection.ID} Op={operation.ID}");

			Exception throwMe = null;
			bool keepAlive;
			try {
				keepAlive = await operation.WaitForCompletion ().ConfigureAwait (false);
			} catch (Exception ex) {
				throwMe = ex;
				keepAlive = false;
			}

			WebConnection.Debug ($"WCG WAIT FOR COMPLETION #1: Cnc={connection.ID} Op={operation.ID} keepAlive={keepAlive} {throwMe?.GetType ()}");

			WebOperation next = null;
			lock (ServicePoint) {
				while (queue.Count > 0 && next == null) {
					next = (WebOperation)queue.Dequeue ();
					if (next.Aborted)
						next = null;
				}
			}

			WebConnection.Debug ($"WCG WAIT FOR COMPLETION DONE: Cnc={connection.ID} Op={operation.ID} keepAlive={keepAlive} next={next?.ID}");

			return (keepAlive, next);
		}

		void RemoveConnection (WebConnection connection)
		{
			WebConnection.Debug ($"WCG REMOVE: Cnc={connection.ID} {connections.Count}");
			var iter = connections.First;
			while (iter != null) {
				var current = iter.Value;
				var node = iter;
				iter = iter.Next;

				if (connection == current) {
					connections.Remove (current);
					return;
				}
			}

			throw new NotImplementedException ();
		}

		WebConnection FindIdleConnection (WebOperation operation)
		{
			WebConnection.Debug ($"WCG FIND IDLE: {connections.Count}");
			foreach (var cnc in connections) {
				if (!cnc.StartOperation (operation, true))
					continue;

				connections.Remove (cnc);
				connections.AddFirst (cnc);
				return cnc;
			}

			return null;
		}

		(WebConnection connection, bool created) CreateOrReuseConnection (WebOperation operation, bool force)
		{
			var connection = FindIdleConnection (operation);
			if (connection != null)
				return (connection, false);

			if (force || ServicePoint.ConnectionLimit > connections.Count || connections.Count == 0) {
				connection = new WebConnection (this, ServicePoint);
				connection.StartOperation (operation, false);
				connections.AddFirst (connection);
				return (connection, true);
			}

			return (null, false);
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
					connectionsToClose.Add (cnc);
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

