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
		}

		int running;
		int maxIdleTime = 100000;
		AsyncManualResetEvent schedulerEvent;

		static int nextId;
		public readonly int ID = ++nextId;

		readonly string ME = $"SPS({ID})";

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
					while (SchedulerIteration ())
						;
				}
			}			
		}

		bool SchedulerIteration ()
		{
			WebConnection.Debug ($"{ME}: ITERATION");
			return false;
		}
	}
}
