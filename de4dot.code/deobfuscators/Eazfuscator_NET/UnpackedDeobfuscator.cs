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
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.Eazfuscator_NET {
	/// <summary>
	/// EazUnpacker 解包之后接手新模块的反混淆器。
	///
	/// 解包产物已经是普通程序集：字符串解密器、资源解析器、内嵌程序集解析器
	/// 这些类型都已被 EazUnpacker 移除。继续跑本模块针对旧版 Eazfuscator 的
	/// 恢复逻辑没有意义，而且会踩到已经置空的状态，所以这里什么都不做——
	/// 只让 de4dot 继续做通用处理（重命名、控制流整理）。
	/// </summary>
	sealed class UnpackedDeobfuscator : DeobfuscatorBase {
		public override string Type => DeobfuscatorInfo.THE_TYPE;
		public override string TypeLong => DeobfuscatorInfo.THE_NAME;
		public override string Name => DeobfuscatorInfo.THE_NAME + " (unpacked)";

		public UnpackedDeobfuscator(OptionsBase options) : base(options) { }

		protected override void ScanForObfuscator() { }

		protected override int DetectInternal() => 0;

		public override IEnumerable<int> GetStringDecrypterMethods() => Array.Empty<int>();
	}
}
