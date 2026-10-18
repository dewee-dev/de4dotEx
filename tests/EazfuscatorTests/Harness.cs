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
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace de4dot.tests {
	/// <summary>
	/// 现代 Eazfuscator.NET 支持的回归测试。
	///
	/// 覆盖两件事：
	///   1. 识别规则（ModernEazfuscator）—— 正例必须命中，两个反例必须不命中；
	///   2. 委托链路（EazUnpackerBridge）—— 解包产物要真的接回流水线，
	///      禁用/解包器不完整时不能崩，仍要输出文件。
	///
	/// 夹具都是普通 C# 源码，用 dotnet build 现编，不依赖 ilasm/ildasm，
	/// 也不需要装 Eazfuscator。
	/// </summary>
	sealed class Harness : IDisposable {
		const int BuildTimeoutMs = 10 * 60 * 1000;
		const int De4dotTimeoutMs = 5 * 60 * 1000;

		readonly string repoRoot;
		readonly string de4dotPath;
		readonly string dotnetPath;
		readonly string workDir;
		readonly bool keepWorkDir;
		readonly string filter;

		readonly List<KeyValuePair<string, Action>> scenarios = new List<KeyValuePair<string, Action>>();
		readonly Dictionary<string, string> fixtureOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>最近一次 de4dot 的输出，断言失败时打出来方便定位。</summary>
		string lastDe4dotOutput;

		public int Passed { get; private set; }
		public int Failed { get; private set; }
		public int Skipped { get; private set; }

		public Harness(string repoRoot, string de4dotPath, string dotnetPath, bool keepWorkDir, string filter) {
			this.repoRoot = repoRoot;
			this.de4dotPath = de4dotPath;
			this.dotnetPath = dotnetPath;
			this.keepWorkDir = keepWorkDir;
			this.filter = filter;

			workDir = Path.Combine(Path.GetTempPath(), "de4dot-eaztests", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
			Directory.CreateDirectory(workDir);
			Console.WriteLine("work dir:   " + workDir);
		}

		public void Add(string name, Action action) =>
			scenarios.Add(new KeyValuePair<string, Action>(name, action));

		public void Execute() {
			foreach (var scenario in scenarios) {
				if (filter != null && scenario.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
					continue;

				Console.WriteLine();
				Console.WriteLine("== " + scenario.Key);
				lastDe4dotOutput = null;
				try {
					scenario.Value();
					Passed++;
					Console.WriteLine("PASS " + scenario.Key);
				}
				catch (SkipException ex) {
					Skipped++;
					Console.WriteLine("SKIP " + scenario.Key + " (" + ex.Message + ")");
				}
				catch (AssertException ex) {
					Failed++;
					Console.WriteLine("FAIL " + scenario.Key);
					Console.WriteLine(Indent(ex.Message));
					DumpDe4dotOutput();
				}
				catch (Exception ex) {
					Failed++;
					Console.WriteLine("FAIL " + scenario.Key);
					Console.WriteLine(Indent(ex.ToString()));
					DumpDe4dotOutput();
				}
			}

			Console.WriteLine();
			Console.WriteLine(Passed + " passed, " + Failed + " failed, " + Skipped + " skipped");
		}

		void DumpDe4dotOutput() {
			if (string.IsNullOrEmpty(lastDe4dotOutput))
				return;
			Console.WriteLine("--- de4dot output ---");
			Console.WriteLine(Indent(lastDe4dotOutput.TrimEnd()));
			Console.WriteLine("--- end of de4dot output ---");
		}

		// ---------------------------------------------------------------- 场景

		/// <summary>正例：internal static string(int) 调用 string(int,bool) → 判为现代版本。</summary>
		public void ModernDetected() {
			var dll = FixtureAssembly("ModernStringDecrypter");
			var result = RunDe4dot("-d", dll);
			Assert.True(result.ExitCode == 0, "de4dot -d exited with " + result.ExitCode);
			Assert.Contains(result.Output, "Eazfuscator.NET (modern, 2021.1+)", "detection output");
		}

		/// <summary>反例：形状相似但缺一个条件，不能被判成 Eazfuscator。</summary>
		public void LookalikeNotDetected() {
			var dll = FixtureAssembly("LookalikeStringMethod");
			var result = RunDe4dot("-d", dll);
			Assert.True(result.ExitCode == 0, "de4dot -d exited with " + result.ExitCode);
			Assert.NotContains(result.Output, "Eazfuscator.NET", "detection output");
		}

		/// <summary>委托链路：解包器交出的程序集必须真的替换掉原始模块。</summary>
		public void BridgeReplacesModule() {
			RequireWindows();

			var input = FixtureAssembly("ModernStringDecrypter");
			var marker = FixtureAssembly("UnpackedMarker");
			var unpackerDir = CreateStubUnpacker(marker, withWorker: true);
			var output = NewOutputPath("bridge-replaces.dll");

			var result = RunDe4dot(input, "-o", output, "--dont-rename", "--eaz-unpacker", unpackerDir);

			Assert.True(result.ExitCode == 0, "de4dot exited with " + result.ExitCode);
			Assert.Contains(result.Output, "EazUnpacker produced", "de4dot log");
			Assert.True(File.Exists(output), "de4dot didn't write " + output);
			Assert.True(Assert.FileContainsText(output, "EazUnpackedMarker"),
				"output should contain the unpacked marker type (the unpacked module wasn't used)");
			Assert.False(Assert.FileContainsText(output, "ModernStringDecrypter"),
				"output still contains the original fixture (the module wasn't replaced)");
		}

		/// <summary>--no-eaz-unpack：只告警，退回通用处理，仍然产出文件。</summary>
		public void BridgeDisabledFallsBack() {
			RequireWindows();

			var input = FixtureAssembly("ModernStringDecrypter");
			var output = NewOutputPath("bridge-disabled.dll");

			var result = RunDe4dot(input, "-o", output, "--dont-rename", "--no-eaz-unpack");

			Assert.True(result.ExitCode == 0, "de4dot exited with " + result.ExitCode);
			Assert.Contains(result.Output, "delegation is disabled", "de4dot log");
			Assert.True(File.Exists(output), "de4dot didn't write " + output);
			Assert.True(Assert.FileContainsText(output, "ModernStringDecrypter"),
				"output should still be the original module when unpacking is disabled");
		}

		/// <summary>
		/// 解包器目录里缺 worker：委托失败，不能抛异常（曾经的 NullReferenceException 回归点）。
		/// </summary>
		public void BridgeIncompleteUnpackerFallsBack() {
			RequireWindows();

			var input = FixtureAssembly("ModernStringDecrypter");
			var marker = FixtureAssembly("UnpackedMarker");
			var unpackerDir = CreateStubUnpacker(marker, withWorker: false);
			var output = NewOutputPath("bridge-incomplete.dll");

			var result = RunDe4dot(input, "-o", output, "--dont-rename", "--eaz-unpacker", unpackerDir);

			Assert.True(result.ExitCode == 0, "de4dot exited with " + result.ExitCode);
			Assert.Contains(result.Output, "Incomplete EazUnpacker", "de4dot log");
			Assert.True(File.Exists(output), "de4dot didn't write " + output);
			Assert.True(Assert.FileContainsText(output, "ModernStringDecrypter"),
				"output should still be the original module when the unpacker is unusable");
		}

		/// <summary>命令行选项必须出现在 -h 里（miscOptions 注册丢失是容易犯的错）。</summary>
		public void HelpListsOptions() {
			var result = RunDe4dot("-h");
			foreach (var option in new[] { "--eaz-unpacker", "--eaz-unpack-timeout", "--no-eaz-unpack", "--keep-eaz-temp" })
				Assert.Contains(result.Output, option, "help output");
		}

		// ------------------------------------------------------------ 辅助方法

		ProcessResult RunDe4dot(params string[] args) {
			var list = new List<string>(args);
			Console.WriteLine("  $ de4dot " + Proc.BuildCommandLine(list));
			var result = Proc.Run(de4dotPath, list, workDir, De4dotTimeoutMs);
			lastDe4dotOutput = result.Output;
			if (result.ExitCode != 0)
				Console.WriteLine(Indent(result.Output));
			return result;
		}

		/// <summary>编译夹具并返回产物所在目录（同一夹具只编一次）。</summary>
		string BuildFixture(string name) {
			string output;
			if (fixtureOutputs.TryGetValue(name, out output))
				return output;

			var project = Path.Combine(repoRoot, "tests", "EazfuscatorTests", "fixtures", name, name + ".csproj");
			Assert.True(File.Exists(project), "fixture project not found: " + project);

			output = Path.Combine(workDir, "fixtures", name);
			var args = new[] { "build", project, "-c", "Release", "-o", output, "--nologo", "-v", "minimal" };
			Console.WriteLine("  $ dotnet " + Proc.BuildCommandLine(args));
			var result = Proc.Run(dotnetPath, args, repoRoot, BuildTimeoutMs);
			if (result.ExitCode != 0)
				throw new AssertException("building fixture " + name + " failed\n" + Indent(result.Output));

			fixtureOutputs[name] = output;
			return output;
		}

		string FixtureAssembly(string name) {
			var path = Path.Combine(BuildFixture(name), name + ".dll");
			Assert.True(File.Exists(path), "fixture assembly not found: " + path);
			return path;
		}

		/// <summary>
		/// 搭一个假的解包器目录：Unpacker-net48.exe + marker.dll（"解包产物"）
		/// + 可选的 worker 占位文件。withWorker=false 用来构造"解包器不完整"。
		/// </summary>
		string CreateStubUnpacker(string markerAssembly, bool withWorker) {
			var stubDir = BuildFixture("StubUnpacker");
			var dir = Path.Combine(workDir, withWorker ? "eaz-unpacker" : "eaz-unpacker-incomplete");
			Directory.CreateDirectory(dir);

			foreach (var file in Directory.GetFiles(stubDir, "Unpacker-net48.*"))
				File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), true);
			File.Copy(markerAssembly, Path.Combine(dir, "marker.dll"), true);

			if (withWorker) {
				var workerDir = Path.Combine(dir, "worker");
				Directory.CreateDirectory(workerDir);
				// 只检查存在性，内容无所谓
				File.WriteAllText(Path.Combine(workerDir, "Unpacker.Worker.exe"), "placeholder");
			}

			return dir;
		}

		string NewOutputPath(string fileName) {
			var dir = Path.Combine(workDir, "out");
			Directory.CreateDirectory(dir);
			return Path.Combine(dir, fileName);
		}

		static void RequireWindows() {
			if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				throw new SkipException("needs a Windows host to run the stub Unpacker-net48.exe");
		}

		static string Indent(string text) {
			if (string.IsNullOrEmpty(text))
				return "  (no output)";
			return "  " + text.Replace("\r\n", "\n").Replace("\n", "\n  ").TrimEnd();
		}

		public void Dispose() {
			if (keepWorkDir) {
				Console.WriteLine("kept work dir: " + workDir);
				return;
			}
			try {
				if (Directory.Exists(workDir))
					Directory.Delete(workDir, true);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				Console.WriteLine("can't delete work dir " + workDir + ": " + ex.Message);
			}
		}
	}
}
