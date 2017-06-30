//
// ServicePointScheduler.cs
//
// Author:
//       Martin Baulig <mabaul@microsoft.com>
//
// Copyright (c) 2017 Xamarin Inc. (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
#define MARTIN_DEBUG
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.ExceptionServices;
using System.Diagnostics;

namespace System.Net
{
	class ServicePointScheduler
	{
		public ServicePoint ServicePoint {
			get;
		}

		public int MaxIdleTime {
			get { return maxIdleTime; }
			set {
				if (value == maxIdleTime)
					return;
				value = maxIdleTime;
				Run (); 
			}
		}

		public ServicePointScheduler (ServicePoint servicePoint)
		{
			ServicePoint = servicePoint;

			schedulerEvent = new AsyncManualResetEvent (false);
			maxIdleTime = servicePoint.MaxIdleTime;
			defaultGroup = new ConnectionGroup (this, string.Empty);
			operations = new LinkedList<WebOperation> (); 
		}

		[Conditional ("MARTIN_DEBUG")]
		void Debug (string message, params object[] args)
		{
			WebConnection.Debug ($"SPS({ID}): {string.Format (message, args)}");
		}

		[Conditional ("MARTIN_DEBUG")]
		void Debug (string message)
		{
			WebConnection.Debug ($"SPS({ID}): {message}");
		}

		int running;
		int maxIdleTime = 100000;
		AsyncManualResetEvent schedulerEvent;
		ConnectionGroup defaultGroup;
		Dictionary<string, ConnectionGroup> groups;
		LinkedList<WebOperation> operations;

		static int nextId;
		public readonly int ID = ++nextId;

		internal string ME {
			get;
		}

		public void Run ()
		{
			if (Interlocked.CompareExchange (ref running, 1, 0) == 0)
				StartScheduler ();

			schedulerEvent.Set ();
		}

		async void StartScheduler ()
		{
			while (true) {
				Debug ($"SCHEDULER");

				// Gather list of currently running operations.
				WebOperation[] operationArray;
				Task[] taskArray;
				lock (ServicePoint) {
					operationArray = new WebOperation[operations.Count];
					operations.CopyTo (operationArray, 0);

					taskArray = new Task[operationArray.Length + 1];
					taskArray[0] = schedulerEvent.WaitAsync (maxIdleTime);
					for (int i = 0; i < operationArray.Length; i++)
						taskArray[i + 1] = operationArray[i].WaitForCompletion (true);
				}

				Debug ($"SCHEDULER #1: {operationArray.Length}");

				var ret = await Task.WhenAny (taskArray).ConfigureAwait (false);

				lock (ServicePoint) {
					if (ret != taskArray[0]) {
						int idx = -1;
						for (int i = 1; i < taskArray.Length; i++) {
							if (ret == taskArray[i]) {
								idx = i;
								break;
							}
						}

						var operation = operationArray[idx - 1];
						Debug ($"SCHEDULER #2: {idx} Op={operation.ID}");
						operations.Remove (operation);
						SchedulerIteration (operation);
						continue;
					}

					Debug ($"SCHEDULER #3");

					schedulerEvent.Reset ();

					bool repeat;
					do {
						repeat = SchedulerIteration (defaultGroup);

						if (groups != null) {
							foreach (var group in groups)
								repeat |= SchedulerIteration (group.Value);
						}
					} while (repeat);
				}
			}
		}

		void SchedulerIteration (WebOperation operation)
		{
			Debug ($"ITERATION: Op={operation.ID}");


		}

		bool SchedulerIteration (ConnectionGroup group)
		{
			Debug ($"ITERATION: group={group.ID}");

			// First, let's clean up.
			group.Cleanup ();

			// Is there anything in the queue?
			var next = group.GetNextOperation ();
			if (next == null)
				return false;

			Debug ($"ITERATION - OPERATION: group={group.ID} Op={next.ID}");

			var (connection, created) = group.CreateOrReuseConnection (next, false);
			if (connection == null) {
				// All connections are currently busy, need to keep it in the queue for now.
				return false;
			}

			Debug ($"ITERATION - OPERATION STARTED: group={group.ID} Op={next.ID} Cnc={connection.ID}");
			operations.AddLast (next);
			return true;
		}

		public void SendRequest (WebOperation operation, string groupName)
		{
			lock (ServicePoint) {
				var group = GetConnectionGroup (groupName);
				Debug ($"SEND REQUEST: Op={operation.ID} group={group.ID}");
				group.EnqueueOperation (operation);
				Run ();
				Debug ($"SEND REQUEST DONE: Op={operation.ID} group={group.ID}");
			}
		}

		ConnectionGroup GetConnectionGroup (string name)
		{
			lock (ServicePoint) {
				if (string.IsNullOrEmpty (name))
					return defaultGroup;

				if (name == null)
					name = "";

				if (groups == null)
					groups = new Dictionary<string, ConnectionGroup> (); 

				if (groups.TryGetValue (name, out ConnectionGroup group))
					return group;

				group = new ConnectionGroup (this, name);
				groups.Add (name, group);
				return group;
			}
		}

		class ConnectionGroup
		{
			public ServicePointScheduler Scheduler {
				get;
			}

			public string Name {
				get;
			}

			public WebConnectionGroup Group {
				get;
			}

			public bool IsDefault => string.IsNullOrEmpty (Name);

			static int nextId;
			public readonly int ID = ++nextId;
			LinkedList<WebConnection> connections;
			LinkedList<WebOperation> queue;

			public ConnectionGroup (ServicePointScheduler scheduler, string name)
			{
				Scheduler = scheduler;
				Name = name;
				Group = new WebConnectionGroup (scheduler.ServicePoint, name);

				connections = new LinkedList<WebConnection> ();
				queue = new LinkedList<WebOperation> (); 
			}

			public void Cleanup ()
			{
				var iter = connections.First;
				while (iter != null) {
					var connection = iter.Value;
					var node = iter;
					iter = iter.Next;

					if (connection.Closed) {
						Scheduler.Debug ($"REMOVING CONNECTION: group={ID} cnc={connection.ID}");
						connections.Remove (node);
					}
				}
			}

			public void EnqueueOperation (WebOperation operation)
			{
				queue.AddLast (operation);
			}

			public WebOperation GetNextOperation ()
			{
				// Is there anything in the queue?
				var iter = queue.First;
				while (iter != null) {
					var operation = iter.Value;
					var node = iter;

					if (operation.Aborted) {
						queue.Remove (node);
						continue;
					}

					return operation;
				}

				return null;
			}

			public WebConnection FindIdleConnection (WebOperation operation)
			{
				foreach (var connection in connections) {
					if (!connection.StartOperation (operation, true))
						continue;

					connections.Remove (connection);
					connections.AddFirst (connection);
					queue.Remove (operation);
					return connection;
				}

				return null;
			}

			public (WebConnection connection, bool created) CreateOrReuseConnection (WebOperation operation, bool force)
			{
				var connection = FindIdleConnection (operation);
				if (connection != null)
					return (connection, false);

				if (force || Scheduler.ServicePoint.ConnectionLimit > connections.Count || connections.Count == 0) {
					connection = new WebConnection (Group, Scheduler.ServicePoint);
					connection.StartOperation (operation, false);
					connections.AddFirst (connection);
					queue.Remove (operation);
					return (connection, true);
				}

				return (null, false);
			}
		}
	}
}
