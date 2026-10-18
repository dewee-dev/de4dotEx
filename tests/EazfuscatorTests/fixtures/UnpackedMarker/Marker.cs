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

namespace UnpackedMarkerFixture {
	// 桩解包器交出的"解包产物"。它和输入夹具是两个完全不同的程序集，
	// 所以只要 de4dot 的输出里出现这个类型名，就证明流水线用的是解包后的
	// 模块，而不是原始文件被原样处理了一遍。
	public sealed class EazUnpackedMarker {
		public int Value;

		public string Describe() => "DE4DOT_TEST_UNPACKED";
	}
}
