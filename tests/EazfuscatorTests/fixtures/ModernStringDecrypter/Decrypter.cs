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

namespace ModernStringDecrypterFixture {
	// 正例：模拟现代 Eazfuscator.NET（2021.1+）的字符串解密器形状。
	// 这是 ModernEazfuscator.FindStringDecrypter 唯一的硬特征：
	//   internal static string M(int) 的方法体里调用同模块的 string(int,bool)。
	// 不需要真的加密，也不依赖 Eazfuscator 本身，只要 IL 形状对就行。
	internal static class Decrypter {
		internal static string Get(int id) => Resolve(id, false);
		internal static string Resolve(int id, bool flag) => flag ? "DE4DOT_TEST_ORIGINAL" : "x" + id;
	}

	public static class Entry {
		public static string Run(int i) => Decrypter.Get(i);
	}
}
