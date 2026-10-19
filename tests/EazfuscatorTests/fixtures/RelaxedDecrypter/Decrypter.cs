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

namespace RelaxedDecrypterFixture {
	// 正例：模拟 2026.2 这类把 string(int,bool) 重载内联/改写掉的解密器。
	// 只有 internal static string(int)，方法体不再调用 string(int,bool)，
	// 所以旧版"必须调用重载"的判定会漏掉它。夹具里靠 Eazfuscator.NET
	// 资源标记补足证据，让现代识别走"退化解密器"分支。
	internal static class Decrypter {
		internal static string Get(int id) => "RELAXED_" + id;
	}

	public static class Entry {
		// 常量参数：内置 worker 只收集 ldc.i4 <id> 紧跟 call 解密器 的调用点。
		public static string Alpha() => Decrypter.Get(3001);
		public static string Beta() => Decrypter.Get(3002);
	}
}
