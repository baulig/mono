﻿using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace WebAssembly.Net.Debugging
{
	public class ProxyOptions {
		public Uri DevToolsUrl { get; set; } = new Uri ("http://localhost:9222");
	}

	public class TestHarnessOptions : ProxyOptions {
		public string ChromePath { get; set; }
		public string AppPath { get; set; }
		public string PagePath { get; set; }
		public string NodeApp { get; set; }
	}

	public class Program {
		public static void Main(string[] args)
		{
			var host = new WebHostBuilder()
				.UseSetting ("UseIISIntegration", false.ToString ())
				.UseKestrel ()
				.UseContentRoot (Directory.GetCurrentDirectory())
				.UseStartup<Startup> ()
				.ConfigureAppConfiguration ((hostingContext, config) =>
				{
					config.AddCommandLine(args);
				})
				.UseUrls ("http://localhost:9300")
				.Build ();

			host.Run ();
		}
	}

	public class TestHarnessProxy {
		static IWebHost host;
		static Task hostTask;
		static CancellationTokenSource cts = new CancellationTokenSource ();
		static object proxyLock = new object ();

		static Uri _webserverUri = null;
		public static Uri Endpoint {
			get {
				if (_webserverUri == null)
					throw new ArgumentException ("Can't use WebServer Uri before it is set, since it is bound dynamically.");

				return _webserverUri;
			}
			set { _webserverUri = value; }
		}

		public static Task Start (string chromePath, string appPath, string pagePath)
		{
			lock (proxyLock) {
				if (host != null)
					return hostTask;

				host = WebHost.CreateDefaultBuilder ()
					.UseSetting ("UseIISIntegration", false.ToString ())
					.ConfigureAppConfiguration ((hostingContext, config) => {
						config.AddEnvironmentVariables (prefix: "WASM_TESTS_");
					})
					.ConfigureServices ((ctx, services) => {
						services.Configure<TestHarnessOptions> (ctx.Configuration);
						services.Configure<TestHarnessOptions> (options => {
							options.ChromePath = options.ChromePath ?? chromePath;
							options.AppPath = appPath;
							options.PagePath = pagePath;
							options.DevToolsUrl = new Uri ("http://localhost:0");
						});
					})
					.UseStartup<TestHarnessStartup> ()
					.UseUrls ("http://127.0.0.1:0")
					.Build();
				hostTask = host.StartAsync (cts.Token);
			}

			Console.WriteLine ("WebServer Ready!");
			return hostTask;
		}
	}
}
