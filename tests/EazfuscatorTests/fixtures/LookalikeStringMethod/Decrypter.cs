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

namespace LookalikeStringMethodFixture {
	// 反例一：签名对（internal static string(int)），但调用的被解密方是
	// string(int) 而不是 string(int,bool)。普通代码里很常见，不应被判成 Eazfuscator。
	internal static class WrongCallee {
		internal static string Get(int id) => Format(id);
		internal static string Format(int id) => "v" + id;
	}

	// 反例二：被调用方是 string(int,bool)（形状对），但解密器本身是 public。
	// Eazfuscator 生成的是 internal，IsAssembly 是判定条件之一，这里必须判否。
	public static class WrongVisibility {
		public static string Get(int id) => Resolve(id, false);
		internal static string Resolve(int id, bool flag) => flag ? "a" : "b" + id;
	}
}
