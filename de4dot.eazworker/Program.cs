/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;

namespace de4dot.eazworker {
	static class Program {
		static int Main(string[] args) {
			if (args.Length == 0) {
				PrintUsage();
				return 2;
			}

			switch (args[0]) {
			case "strings":
				return StringRecovery.Run(args);
			case "-h":
			case "--help":
			case "/?":
				PrintUsage();
				return 0;
			default:
				Console.Error.WriteLine("unknown command: " + args[0]);
				PrintUsage();
				return 2;
			}
		}

		static void PrintUsage() {
			Console.WriteLine("de4dot.eazworker - string recovery backend for modern Eazfuscator.NET");
			Console.WriteLine();
			Console.WriteLine("Usage:");
			Console.WriteLine("  de4dot.eazworker strings <input> <output> [--timeout <seconds>] [--keep]");
			Console.WriteLine();
			Console.WriteLine("  <input>    obfuscated assembly (the original is never modified)");
			Console.WriteLine("  <output>   map file written as: <id> TAB <base64 of UTF-16LE string>");
			Console.WriteLine("  --timeout  how long the probe process may run (default: 120)");
			Console.WriteLine("  --keep     keep the generated probe next to the input for troubleshooting");
			Console.WriteLine();
			Console.WriteLine("Exit codes: 0 success, 1 recovery failed, 2 usage/IO error.");
			Console.WriteLine();
			Console.WriteLine("This tool runs the sample: it injects a probe into a copy of the assembly,");
			Console.WriteLine("executes that copy in a child process, and reads the decrypted strings back.");
		}
	}
}
