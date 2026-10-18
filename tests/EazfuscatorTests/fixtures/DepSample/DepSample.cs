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

using DepLibrary;

namespace DepSampleFixture {
	// 现代 Eazfuscator.NET 解密器形状（正例），但解密结果依赖一个外部程序集 DepLibrary。
	// 用来回归"样本带了不在同目录的第三方依赖"时，内置 worker 必须照样能恢复字符串。
	internal static class Decrypter {
		internal static string Get(int id) => Resolve(id, false);
		internal static string Resolve(int id, bool flag) => flag ? "flag" : Marker.Value() + "_" + id;
	}

	public static class Entry {
		public static string A() => Decrypter.Get(2001);
		public static string B() => Decrypter.Get(2002);
	}
}
