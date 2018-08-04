//
// pinvoke-checker.cs
//
// Authors:
//	Martin Baulig <mabaul@microsoft.com>
//
// Copyright (C) 2018 Xamarin Inc (http://www.xamarin.com)
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

using System;
using System.IO;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Options;
using System.Collections.Generic;

public class Program
{
	class CmdOptions
	{
		public bool ShowHelp { get; set; }
		public bool Verbose { get; set; }
	}

	public static int Main (string[] args)
	{
		var options = new CmdOptions ();

		var p = new OptionSet () {
			{ "h|help",  "Display available options",
				v => options.ShowHelp = v != null },
			{ "v|verbose",  "Use verbose output",
				v => options.Verbose = v != null },
		};

		List<string> extra;
		try {
			extra = p.Parse (args);
		}
		catch (OptionException e) {
			Console.WriteLine (e.Message);
			Console.WriteLine ("Try 'pinvoke-checker -help' for more information.");
			return 1;
		}

		if (options.ShowHelp) {
			ShowHelp (p);
			return 0;
		}

		if (extra.Count != 1) {
			ShowHelp (p);
			return 2;
		}

		CheckAssembly (extra [0], options);

		return 0;
	}

	static void ShowHelp (OptionSet p)
	{
		Console.WriteLine ("Usage: pinvoke-checker [options] assembly");
		Console.WriteLine ("Checks whether we don't have any missing P/Invokes.");
		Console.WriteLine ();
		Console.WriteLine ("Options:");
		p.WriteOptionDescriptions (Console.Out);
	}

	static void CheckAssembly (string assemblyLocation, CmdOptions options)
	{
		var readerParameters = new ReaderParameters {
			ReadSymbols = true,
			ReadWrite = true,
			SymbolReaderProvider = new DefaultSymbolReaderProvider (false)
		};

		using (var assembly = AssemblyDefinition.ReadAssembly (assemblyLocation, readerParameters)) {
			foreach (var module in assembly.Modules) {
				foreach (var type in module.GetTypes ()) {
					foreach (var method in type.Methods) {
					}
				}
			}

		}
	}
}