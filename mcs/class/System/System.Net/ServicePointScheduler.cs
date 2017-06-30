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
		}

		[Conditional ("MARTIN_DEBUG")]
		static void Debug (string message, params object[] args)
		{
			WebConnection.Debug ($"SPS({ID}): {string.Format (message, args)}");
		}

		[Conditional ("MARTIN_DEBUG")]
		internal static void Debug (string message)
		{
			WebConnection.Debug ($"SPS({ID}): {message}");
		}

		int running;
		int maxIdleTime = 100000;
		AsyncManualResetEvent schedulerEvent;
		ConnectionGroup defaultGroup;
		Dictionary<string, ConnectionGroup> groups;

		static int nextId;
		public readonly int ID = ++nextId;

		internal string ME {
			get;
		}

		public void Run ()
		{
			if (Interlocked.CompareExchange (ref running, 1, 0) != 0) {
				schedulerEvent.Set ();
				return;
			}

			StartScheduler ();
		}

		async void StartScheduler ()
		{
			while (true) {
				var ret = await schedulerEvent.WaitAsync (maxIdleTime);

				lock (ServicePoint) {
					bool repeat;
					do {
						repeat = SchedulerIteration (defaultGroup);

						if (groups != null) {
							foreach (var group in groups)
								repeat |= SchedulerIteration (group);
						}
					} while (repeat);
				}
			}
		}

		bool SchedulerIteration (ConnectionGroup group)
		{
			Debug ($"ITERATION: group={group.ID}");
			return false;
		}

		public void SendRequest (WebOperation operation, string groupName)
		{
			lock (ServicePoint) {
				var group = GetConnectionGroup (groupName);
				Debug ($"SEND REQUEST: Op={operation.ID} group={group.ID}");

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

			public ConnectionGroup (ServicePointScheduler scheduler, string name)
			{
				Scheduler = scheduler;
				Name = name;
				Group = new WebConnectionGroup (scheduler.ServicePoint, name);
			}
		}
	}
}
