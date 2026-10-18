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
	/// <summary>
	/// 外部 EazUnpacker 的配置。命令行参数会写入这里，未设置时走自动发现。
	/// 之所以用静态配置：de4dot 的 IDeobfuscator 由 DeobfuscatorInfo 无参构造，
	/// 没有把命令行选项传进具体反混淆器的通道（其它模块的选项也是这么处理的）。
	/// </summary>
	public static class EazUnpackerSettings {
		/// <summary>是否在识别到现代 Eazfuscator 时自动调用 EazUnpacker。</summary>
		public static bool Enabled { get; set; } = true;
		/// <summary>Unpacker-net48.exe 的完整路径，或它所在的目录。null 表示自动发现。</summary>
		public static string UnpackerPath { get; set; }
		/// <summary>单次解包的超时时间。</summary>
		public static int TimeoutSeconds { get; set; } = 1800;
		/// <summary>保留临时输出目录，便于排查解包本身的问题。</summary>
		public static bool KeepTempFiles { get; set; }
	}

	/// <summary>
	/// 把现代 Eazfuscator 程序集交给外部 EazUnpacker 解包，再把结果读回内存。
	///
	/// 这里刻意用子进程而不是把 EazUnpacker 的代码链进来：EazUnpacker 的恢复流程
	/// 本来就是围绕 worker 进程和磁盘文件设计的（dnlib/AsmResolver 版本也和本仓库不同），
	/// 进程边界既避免了依赖冲突，也保证解包结果与命令行完全一致。
	/// </summary>
	static class EazUnpackerBridge {
		const string ExeName = "Unpacker-net48.exe";
		const string WorkerRelativePath = @"worker\Unpacker.Worker.exe";
		const string EnvVarName = "EAZ_UNPACKER";

		/// <summary>找到可用的 Unpacker-net48.exe，找不到返回 null。</summary>
		public static string ResolveUnpacker() {
			var configured = EazUnpackerSettings.UnpackerPath;
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
				foreach (var candidate in new[] {
					Path.Combine(baseDir, "eaz-unpacker", ExeName),
					Path.Combine(baseDir, ExeName),
				}) {
					if (File.Exists(candidate))
						return candidate;
				}
			}

			return FindOnPath(ExeName);
		}

		/// <summary>解释用户给的路径：可以是 exe，也可以是包含 exe 的目录。</summary>
		static string ResolvePath(string path) {
			try {
				if (Directory.Exists(path)) {
					var candidate = Path.Combine(path, ExeName);
					return File.Exists(candidate) ? candidate : null;
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
				var location = typeof(EazUnpackerBridge).Assembly.Location;
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

		/// <summary>
		/// 解包一个文件。成功时 data 为解包后程序集的字节，error 为 null。
		/// 失败不会抛异常——调用方会退回原本的（未解包）模块继续处理。
		/// </summary>
		public static bool TryUnpack(string inputFile, out byte[] data, out string error) {
			data = null;
			error = null;

			var exe = ResolveUnpacker();
			if (exe == null) {
				error = $"Can't find {ExeName}. Use --eaz-unpacker <path>, set the {EnvVarName} environment variable, " +
					"or put it in an eaz-unpacker\\ subdirectory next to de4dot.";
				return false;
			}

			var worker = Path.Combine(Path.GetDirectoryName(exe) ?? "", WorkerRelativePath);
			if (!File.Exists(worker)) {
				error = $"Incomplete EazUnpacker: missing {WorkerRelativePath} under {Path.GetDirectoryName(exe)}.";
				return false;
			}

			var tempDir = Path.Combine(Path.GetTempPath(), "de4dot-eaz", Guid.NewGuid().ToString("N"));
			var outputFile = Path.Combine(
				tempDir,
				Path.GetFileNameWithoutExtension(inputFile) + ".unpacked" + Path.GetExtension(inputFile));

			try {
				Directory.CreateDirectory(tempDir);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				error = $"Can't create temp directory {tempDir}: {ex.Message}";
				return false;
			}

			try {
				if (!RunUnpacker(exe, inputFile, outputFile, out error))
					return false;

				if (!File.Exists(outputFile)) {
					error = "EazUnpacker didn't produce an output file.";
					return false;
				}

				data = File.ReadAllBytes(outputFile);
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				error = ex.Message;
				return false;
			}
			finally {
				if (EazUnpackerSettings.KeepTempFiles)
					Logger.v("Keeping EazUnpacker temp directory: {0}", tempDir);
				else
					TryDeleteDirectory(tempDir);
			}
		}

		static bool RunUnpacker(string exe, string inputFile, string outputFile, out string error) {
			error = null;
			var log = new List<string>();
			var gate = new object();

			void Receive(string line) {
				if (line == null)
					return;
				lock (gate) {
					log.Add(line);
					if (log.Count > 200)
						log.RemoveAt(0);
				}
				Logger.v("[eaz] {0}", line);
			}

			var psi = new ProcessStartInfo(exe) {
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = new UTF8Encoding(false),
				StandardErrorEncoding = new UTF8Encoding(false),
				WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
				Arguments = "unpack " + Quote(inputFile) + " " + Quote(outputFile),
			};

			using (var process = new Process { StartInfo = psi }) {
				process.OutputDataReceived += (s, e) => Receive(e.Data);
				process.ErrorDataReceived += (s, e) => Receive(e.Data);

				try {
					process.Start();
				}
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) {
					error = $"Can't start {exe}: {ex.Message}";
					return false;
				}

				process.BeginOutputReadLine();
				process.BeginErrorReadLine();

				var timeoutMs = Math.Max(1, EazUnpackerSettings.TimeoutSeconds) * 1000;
				bool exited;
				try {
					exited = process.WaitForExit(timeoutMs);
				}
				catch (Exception ex) when (ex is InvalidOperationException || ex is SystemException) {
					error = $"Error while waiting for EazUnpacker: {ex.Message}";
					return false;
				}

				if (!exited) {
					KillTree(process);
					error = $"EazUnpacker didn't finish within {EazUnpackerSettings.TimeoutSeconds}s and was terminated.";
					return false;
				}

				process.WaitForExit();

				// 0 = 完成，2 = 部分完成（仍然会产出可用的程序集），其它视为失败。
				if (process.ExitCode != 0 && process.ExitCode != 2) {
					error = $"EazUnpacker exited with code {process.ExitCode}.{Tail(log, gate)}";
					return false;
				}

				if (process.ExitCode == 2)
					Logger.w("EazUnpacker reported partial completion (exit code 2); using the assembly it produced.");

				return true;
			}
		}

		static string Tail(List<string> log, object gate) {
			lock (gate) {
				if (log.Count == 0)
					return "";
				var start = Math.Max(0, log.Count - 5);
				var sb = new StringBuilder(" Last lines:");
				for (int i = start; i < log.Count; i++)
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

		static void TryDeleteDirectory(string dir) {
			try {
				if (Directory.Exists(dir))
					Directory.Delete(dir, true);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				Logger.v("Can't delete temp directory {0}: {1}", dir, ex.Message);
			}
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
