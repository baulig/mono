// Based on https://raw.githubusercontent.com/xunit/samples.xunit/master/TestRunner/Program.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit.Abstractions;
using Xunit;
using Xunit.Sdk;
using Xunit.Runners;


namespace PhoneTest
{
	public class DeviceRunner
	{
		static object consoleLock = new object();
		ManualResetEvent finished = new ManualResetEvent(false);
		int result = 0;
		int countFailed;
		int countPassed;
		int countSkipped;

		readonly AssemblyRunner runner;
		readonly XunitFilters filters;

		public DeviceRunner ()
		{
			var assembly = Assembly.GetEntryAssembly ().Location;
			runner = AssemblyRunner.WithoutAppDomain (assembly);
			filters = new XunitFilters ();

			runner.OnDiscoveryComplete = OnDiscoveryComplete;
			runner.OnExecutionComplete = OnExecutionComplete;
			runner.OnTestFailed = OnTestFailed;
			runner.OnTestPassed = OnTestPassed;
			runner.OnTestSkipped = OnTestSkipped;

			runner.TestCaseFilter = filters.Filter;
		}

		public XunitFilters Filters => filters;

		public void Run ()
		{
			Console.WriteLine ("Discovering...");
			runner.Start ();

			finished.WaitOne ();
			finished.Dispose ();
		}

		void OnDiscoveryComplete (DiscoveryCompleteInfo info)
		{
			lock (consoleLock)
				Console.WriteLine ($"Running {info.TestCasesToRun} of {info.TestCasesDiscovered} tests...");
		}

		void OnExecutionComplete (ExecutionCompleteInfo info)
		{
			lock (consoleLock) {
				Console.WriteLine ();
				Console.WriteLine ($"Tests run: {info.TotalTests}, Passed: {countPassed} Failed: {countFailed} Skipped: {countSkipped}");
				Console.WriteLine ($"Total time: {Math.Round(info.ExecutionTime, 3)}s.");
				finished.Set ();
			}
		}

		void OnTestFailed (TestFailedInfo info)
		{
			lock (consoleLock)
			{
				++countFailed;
				Console.ForegroundColor = ConsoleColor.Red;

				Console.WriteLine ($"\t[FAIL] {info.TestDisplayName}: {info.ExceptionMessage}");
				if (info.ExceptionStackTrace != null)
					Console.WriteLine (info.ExceptionStackTrace);

				Console.ResetColor ();
			}

			result = 1;
		}

		void OnTestPassed (TestPassedInfo info)
		{
			lock (consoleLock)
			{
				++countPassed;
				Console.WriteLine ($"\t[PASS] {info.TestDisplayName}");
			}
		}

		void OnTestSkipped (TestSkippedInfo info)
		{
			lock (consoleLock)
			{
				++countSkipped;
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine ($"\t[SKIP] {info.TestDisplayName}: {info.SkipReason}");
				Console.ResetColor();
			}
		}
	}
}
