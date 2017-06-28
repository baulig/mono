//
// WebConnectionState.cs
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
	class WebConnectionState
	{
		public WebConnection Connection {
			get;
		}

		public WebConnectionGroup Group {
			get;
		}

		public ServicePoint ServicePoint => Group.ServicePoint;

		DateTime idleSince;
		WebOperation currentOperation;

		public bool Busy {
			get { return currentOperation != null; }
		}

		public DateTime IdleSince {
			get { return idleSince; }
		}

		public WebConnectionState (WebConnectionGroup group)
		{
			Group = group;
			idleSince = DateTime.UtcNow;
			Connection = new WebConnection (this, group.ServicePoint);
		}

		public bool StartOperation (WebOperation operation, bool reused)
		{
			if (Interlocked.CompareExchange (ref currentOperation, operation, null) != null)
				return false;

			idleSince = DateTime.UtcNow + TimeSpan.FromDays (3650);

			if (reused)
				Connection.PrepareSharingNtlm (operation);
			operation.Run (ServicePoint, Connection);
			return true;
		}

		public void Continue (bool keepAlive, WebOperation next)
		{
			if (!keepAlive) {
				try {
					Connection.ReallyCloseIt ();
				} catch { }
			}

			currentOperation = next;
			if (next == null) {
				idleSince = DateTime.UtcNow;
				return;
			}

			if (keepAlive)
				Connection.PrepareSharingNtlm (next);
			next.Run (ServicePoint, Connection);
		}
	}
}
