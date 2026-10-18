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
using System.Text;

namespace de4dot.tests {
	sealed class ProcessResult {
		public int ExitCode;
		public string Output;
		public string Command;

		public override string ToString() => Command + " => exit " + ExitCode;
	}

	/// <summary>启动子进程并收集 stdout + stderr。</summary>
	static class Proc {
		public static ProcessResult Run(string exe, IEnumerable<string> args, string workingDir, int timeoutMs) {
			var commandLine = BuildCommandLine(args);
			var psi = new ProcessStartInfo(exe, commandLine) {
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = workingDir,
			};

			var output = new StringBuilder();
			var gate = new object();

			using (var process = new Process { StartInfo = psi }) {
				process.OutputDataReceived += (sender, e) => Append(output, gate, e.Data);
				process.ErrorDataReceived += (sender, e) => Append(output, gate, e.Data);

				process.Start();
				// de4dot 在"看起来像双击运行"的环境里会 ReadKey 等按键；把 stdin 关掉，
				// 它内部的 InvalidOperationException 会被吞掉，不会把测试挂住。
				process.StandardInput.Close();
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();

				if (!process.WaitForExit(timeoutMs)) {
					try {
						process.Kill();
					}
					catch (Exception) {
						// 杀不掉也没关系，下面会抛超时
					}
					throw new AssertException("timed out after " + timeoutMs + "ms: " + exe + " " + commandLine);
				}

				// 让异步读取把剩余的缓冲刷完
				process.WaitForExit();

				return new ProcessResult {
					ExitCode = process.ExitCode,
					Output = output.ToString(),
					Command = exe + " " + commandLine,
				};
			}
		}

		static void Append(StringBuilder output, object gate, string line) {
			if (line == null)
				return;
			lock (gate)
				output.AppendLine(line);
		}

		public static string BuildCommandLine(IEnumerable<string> args) {
			var sb = new StringBuilder();
			foreach (var arg in args) {
				if (sb.Length > 0)
					sb.Append(' ');
				sb.Append(Quote(arg));
			}
			return sb.ToString();
		}

		/// <summary>Windows 命令行引用规则（CommandLineToArgvW），含结尾反斜杠与内嵌引号。</summary>
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
				else if (c == '"')
					sb.Append("\\\"");
				else
					sb.Append(c);
			}
			sb.Append('"');
			return sb.ToString();
		}
	}
}
