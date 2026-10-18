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

namespace StubUnpacker {
	/// <summary>
	/// 假的 EazUnpacker：把同目录下的 marker.dll 复制到 <c>unpack &lt;in&gt; &lt;out&gt;</c>
	/// 的 out 路径，然后以 0 退出。
	///
	/// 存在的意义是让 de4dot 的委托链路可以脱离真实的 EazUnpacker 被验证：
	/// 输出文件里出现 marker 的类型名，就说明 de4dot 确实换了模块。
	/// 命令行契约与真实 Unpacker-net48.exe 一致（unpack &lt;in&gt; &lt;out&gt;）。
	/// </summary>
	internal static class Program {
		static int Main(string[] args) {
			Console.WriteLine("[stub] " + string.Join(" ", args));

			if (args.Length != 3 || args[0] != "unpack") {
				Console.Error.WriteLine("usage: Unpacker-net48.exe unpack <input> <output>");
				return 1;
			}

			var marker = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "marker.dll");
			if (!File.Exists(marker)) {
				Console.Error.WriteLine("marker.dll not found next to the stub: " + marker);
				return 1;
			}

			try {
				File.Copy(marker, args[2], true);
			}
			catch (Exception ex) {
				Console.Error.WriteLine("can't write " + args[2] + ": " + ex.Message);
				return 1;
			}

			Console.WriteLine("[stub] wrote " + args[2]);
			return 0;
		}
	}
}
