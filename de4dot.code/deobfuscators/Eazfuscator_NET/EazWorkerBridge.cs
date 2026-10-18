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
using System.Diagnostics;
using System.IO;
using System.Text;

namespace de4dot.code.deobfuscators.Eazfuscator_NET {
	/// <summary>内置解包后端（de4dot.eazworker）的配置。</summary>
	public static class EazWorkerSettings {
		/// <summary>是否使用内置后端恢复字符串。</summary>
		public static bool Enabled { get; set; } = true;
		/// <summary>de4dot.eazworker.exe 的路径，或它所在的目录。null 表示自动发现。</summary>
		public static string WorkerPath { get; set; }
		/// <summary>探针进程的超时时间。</summary>
		public static int TimeoutSeconds { get; set; } = 120;
	}

	/// <summary>
	/// 调用内置的 de4dot.eazworker 恢复现代 Eazfuscator 的字符串。
	///
	/// 和 EazUnpackerBridge 的区别：这里只恢复字符串，不换模块。拿到 id → 明文 的表之后，
	/// 由 Deobfuscator 注册进 staticStringInliner，de4dot 自己完成替换，
	/// 后续的重命名、控制流处理全部照旧。
	///
	/// worker 必须把样本当独立进程跑一次（解密器的反调试只认静态调用），
	/// 所以这里仍然有进程边界，只是不再依赖外部项目。
	/// </summary>
	static class EazWorkerBridge {
		const string ExeName = "de4dot.eazworker.exe";
		const string ExeNameNoExt = "de4dot.eazworker";
		const string EnvVarName = "EAZ_WORKER";

		/// <summary>找到可用的 worker，找不到返回 null。</summary>
		public static string ResolveWorker() {
			var configured = EazWorkerSettings.WorkerPath;
			if (!string.IsNullOrEmpty(configured)) {
				var resolved = ResolvePath(configured);
				if (resolved != null)
					return resolved;
			}

			var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
			if (!string.IsNullOrEmpty(fromEnv)) {
				var resolved = ResolvePath(fromEnv);
				if (resolved != null)
					return resolved;
			}

			var baseDir = GetBaseDirectory();
			if (baseDir != null) {
				foreach (var name in new[] { ExeName, ExeNameNoExt }) {
					foreach (var candidate in new[] {
						Path.Combine(baseDir, "eaz-worker", name),
						Path.Combine(baseDir, name),
					}) {
						if (File.Exists(candidate))
							return candidate;
					}
				}
			}

			return FindOnPath(ExeName) ?? FindOnPath(ExeNameNoExt);
		}

		/// <summary>
		/// 恢复字符串。失败时返回 false 并给出原因，调用方会退回到外部 EazUnpacker。
		/// </summary>
		public static bool TryRecoverStrings(string inputFile, out Dictionary<int, string> strings, out string error) {
			strings = null;
			error = null;

			var exe = ResolveWorker();
			if (exe == null) {
				error = "can't find " + ExeName + " (use --eaz-worker <path>, set " + EnvVarName +
					", or put it next to de4dot)";
				return false;
			}

			var mapFile = Path.Combine(Path.GetTempPath(), "de4dot-eazworker", Guid.NewGuid().ToString("N") + ".txt");
			try {
				Directory.CreateDirectory(Path.GetDirectoryName(mapFile));
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				error = "can't create temp directory: " + ex.Message;
				return false;
			}

			try {
				var args = new List<string> {
					"strings", inputFile, mapFile,
					"--timeout", EazWorkerSettings.TimeoutSeconds.ToString(),
				};

				if (!RunWorker(exe, args, EazWorkerSettings.TimeoutSeconds + 60, out error))
					return false;

				if (!File.Exists(mapFile)) {
					error = "the worker didn't write a map file";
					return false;
				}

				strings = ReadMap(mapFile);
				if (strings.Count == 0) {
					error = "the worker recovered no strings";
					strings = null;
					return false;
				}

				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				error = ex.Message;
				return false;
			}
			finally {
				TryDelete(mapFile);
			}
		}

		static bool RunWorker(string exe, List<string> args, int timeoutSeconds, out string error) {
			error = null;
			var tail = new List<string>();
			var gate = new object();

			void Receive(string line) {
				if (line == null)
					return;
				lock (gate) {
					tail.Add(line);
					if (tail.Count > 40)
						tail.RemoveAt(0);
				}
				Logger.v("[eaz-worker] {0}", line);
			}

			var psi = new ProcessStartInfo(exe) {
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false),
				WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
				Arguments = QuoteArgs(args),
			};

			using (var process = new Process { StartInfo = psi }) {
				process.OutputDataReceived += (s, e) => Receive(e.Data);
				process.ErrorDataReceived += (s, e) => Receive(e.Data);

				try {
					process.Start();
				}
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) {
					error = "can't start " + exe + ": " + ex.Message;
					return false;
				}

				// worker 自己的 stdin 用不到，关掉以免它被意外阻塞
				try {
					process.StandardInput.Close();
				}
				catch (Exception) {
					// 已退出，忽略
				}

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();

				bool exited;
				try {
					exited = process.WaitForExit(Math.Max(1, timeoutSeconds) * 1000);
				}
				catch (Exception ex) when (ex is InvalidOperationException || ex is SystemException) {
					error = "error while waiting for the worker: " + ex.Message;
					return false;
				}

				if (!exited) {
					KillTree(process);
					error = "the worker didn't finish within " + timeoutSeconds + "s";
					return false;
				}

				process.WaitForExit();

				if (process.ExitCode != 0) {
					error = "the worker exited with code " + process.ExitCode + Tail(tail, gate);
					return false;
				}

				return true;
			}
		}

		static Dictionary<int, string> ReadMap(string path) {
			var strings = new Dictionary<int, string>();
			foreach (var line in File.ReadAllLines(path)) {
				if (line.Length == 0 || line[0] == '#')
					continue;
				int tab = line.IndexOf('\t');
				if (tab <= 0)
					continue;
				int id;
				if (!int.TryParse(line.Substring(0, tab), out id))
					continue;
				try {
					strings[id] = Encoding.Unicode.GetString(Convert.FromBase64String(line.Substring(tab + 1)));
				}
				catch (FormatException) {
					// 单条损坏就跳过，不影响其它条目
				}
			}
			return strings;
		}

		static string Tail(List<string> log, object gate) {
			lock (gate) {
				if (log.Count == 0)
					return "";
				var sb = new StringBuilder(" Last lines:");
				for (int i = Math.Max(0, log.Count - 5); i < log.Count; i++)
					sb.Append("\n  ").Append(log[i]);
				return sb.ToString();
			}
		}

		static void KillTree(Process process) {
			try {
				var killer = new ProcessStartInfo(
					Path.Combine(Environment.SystemDirectory, "taskkill.exe"),
					"/PID " + process.Id + " /T /F") {
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};
				using (var kill = Process.Start(killer)) {
					if (kill != null) {
						kill.BeginOutputReadLine();
						kill.BeginErrorReadLine();
						kill.WaitForExit(5000);
					}
				}
			}
			catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) {
				// 终止失败不影响主流程
			}
		}

		static void TryDelete(string path) {
			try {
				if (File.Exists(path))
					File.Delete(path);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				Logger.v("Can't delete {0}: {1}", path, ex.Message);
			}
		}

		static string ResolvePath(string path) {
			try {
				if (Directory.Exists(path)) {
					foreach (var name in new[] { ExeName, ExeNameNoExt }) {
						var candidate = Path.Combine(path, name);
						if (File.Exists(candidate))
							return candidate;
					}
					return null;
				}
				return File.Exists(path) ? path : null;
			}
			catch (ArgumentException) {
				return null;
			}
			catch (IOException) {
				return null;
			}
		}

		static string GetBaseDirectory() {
			try {
				var location = typeof(EazWorkerBridge).Assembly.Location;
				return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
			}
			catch (NotSupportedException) {
				return null;
			}
		}

		static string FindOnPath(string exeName) {
			var path = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrEmpty(path))
				return null;
			foreach (var dir in path.Split(Path.PathSeparator)) {
				if (string.IsNullOrWhiteSpace(dir))
					continue;
				try {
					var candidate = Path.Combine(dir.Trim(), exeName);
					if (File.Exists(candidate))
						return candidate;
				}
				catch (ArgumentException) {
					// PATH 里有非法字符，跳过
				}
			}
			return null;
		}

		static string QuoteArgs(IEnumerable<string> args) {
			var sb = new StringBuilder();
			foreach (var arg in args) {
				if (sb.Length > 0)
					sb.Append(' ');
				sb.Append(Quote(arg));
			}
			return sb.ToString();
		}

		/// <summary>Windows 命令行引号规则，含结尾反斜杠与内嵌引号。</summary>
		static string Quote(string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
				return arg;
			var sb = new StringBuilder("\"");
			for (int i = 0; i < arg.Length; i++) {
				char c = arg[i];
				if (c == '\\') {
					int slashes = 1;
					while (i + 1 < arg.Length && arg[i + 1] == '\\') {
						i++;
						slashes++;
					}
					sb.Append('\\', i == arg.Length - 1 ? slashes * 2 : slashes);
				}
				else if (c == '"') {
					sb.Append("\\\"");
				}
				else {
					sb.Append(c);
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}
}
