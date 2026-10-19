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
using System.IO;

namespace de4dot.tests {
	sealed class Options {
		public string De4dotPath;
		public string DotnetPath = "dotnet";
		public bool Keep;
		public string Filter;

		/// <summary>返回 null 表示用户要的是用法说明。</summary>
		public static Options Parse(string[] args) {
			var options = new Options();
			for (int i = 0; i < args.Length; i++) {
				var arg = args[i];
				switch (arg) {
				case "-h":
				case "--help":
				case "/?":
					return null;
				case "--de4dot":
					options.De4dotPath = Next(args, ref i, arg);
					break;
				case "--dotnet":
					options.DotnetPath = Next(args, ref i, arg);
					break;
				case "--filter":
					options.Filter = Next(args, ref i, arg);
					break;
				case "--keep":
					options.Keep = true;
					break;
				default:
					throw new ArgumentException("unknown argument: " + arg);
				}
			}
			return options;
		}

		static string Next(string[] args, ref int i, string option) {
			if (i + 1 >= args.Length)
				throw new ArgumentException(option + " needs a value");
			return args[++i];
		}

		public static void PrintUsage() {
			Console.WriteLine("Usage: EazfuscatorTests [options]");
			Console.WriteLine();
			Console.WriteLine("  --de4dot <path>   de4dot 可执行文件；默认在仓库里找 Release/ 或 publish-*/");
			Console.WriteLine("  --dotnet <path>   用来编译夹具的 dotnet；默认 PATH 里的 dotnet");
			Console.WriteLine("  --filter <text>   只跑名字里含 <text> 的场景");
			Console.WriteLine("  --keep            保留临时工作目录，便于排查");
			Console.WriteLine("  -h, --help        显示本说明");
			Console.WriteLine();
			Console.WriteLine("退出码：0 全部通过（跳过不算失败）、1 有失败、2 用法或环境错误。");
		}
	}

	static class Locate {
		/// <summary>从当前程序集所在目录往上找 de4dot.netcore.sln。</summary>
		public static string RepoRoot() {
			var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
			while (dir != null) {
				if (File.Exists(Path.Combine(dir.FullName, "de4dot.netcore.sln")))
					return dir.FullName;
				dir = dir.Parent;
			}

			dir = new DirectoryInfo(Directory.GetCurrentDirectory());
			while (dir != null) {
				if (File.Exists(Path.Combine(dir.FullName, "de4dot.netcore.sln")))
					return dir.FullName;
				dir = dir.Parent;
			}

			return null;
		}

		public static string De4dot(string repoRoot) {
			var candidates = new[] {
				Path.Combine(repoRoot, "Release", "net48", "de4dot.exe"),
				Path.Combine(repoRoot, "Release", "net10.0", "de4dot.exe"),
				Path.Combine(repoRoot, "publish-net10.0", "de4dot.exe"),
				Path.Combine(repoRoot, "publish-net10.0", "de4dot"),
				Path.Combine(repoRoot, "publish-net48", "de4dot.exe"),
			};
			foreach (var candidate in candidates) {
				if (File.Exists(candidate))
					return candidate;
			}
			return null;
		}
	}

	static class Program {
		static int Main(string[] args) {
			Options options;
			try {
				options = Options.Parse(args);
			}
			catch (ArgumentException ex) {
				Console.Error.WriteLine(ex.Message);
				Console.Error.WriteLine();
				Options.PrintUsage();
				return 2;
			}

			if (options == null) {
				Options.PrintUsage();
				return 0;
			}

			var repoRoot = Locate.RepoRoot();
			if (repoRoot == null) {
				Console.Error.WriteLine("Can't find the repository root (looked for de4dot.netcore.sln).");
				return 2;
			}

			var de4dot = options.De4dotPath ?? Locate.De4dot(repoRoot);
			if (de4dot == null) {
				Console.Error.WriteLine("Can't find de4dot. Build it first, or pass --de4dot <path>.");
				return 2;
			}
			if (!File.Exists(de4dot)) {
				Console.Error.WriteLine("de4dot not found: " + de4dot);
				return 2;
			}

			Console.WriteLine("repository: " + repoRoot);
			Console.WriteLine("de4dot:     " + Path.GetFullPath(de4dot));
			Console.WriteLine("dotnet:     " + options.DotnetPath);

			using (var harness = new Harness(repoRoot, Path.GetFullPath(de4dot), options.DotnetPath, options.Keep, options.Filter)) {
				harness.Add("modern-detected", harness.ModernDetected);
				harness.Add("lookalike-not-detected", harness.LookalikeNotDetected);
				harness.Add("worker-recovers-strings", harness.WorkerRecoversStrings);
				harness.Add("worker-recovers-strings-with-dependency", harness.WorkerRecoversStringsWithDependency);
				harness.Add("worker-recovers-strings-relaxed-decrypter", harness.WorkerRecoversStringsRelaxedDecrypter);
				harness.Add("bridge-replaces-module", harness.BridgeReplacesModule);
				harness.Add("bridge-disabled-falls-back", harness.BridgeDisabledFallsBack);
				harness.Add("bridge-incomplete-unpacker-falls-back", harness.BridgeIncompleteUnpackerFallsBack);
				harness.Add("help-lists-options", harness.HelpListsOptions);

				harness.Execute();
				return harness.Failed == 0 ? 0 : 1;
			}
		}
	}
}
