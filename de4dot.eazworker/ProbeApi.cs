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
using System.Text;
using dnlib.DotNet;

namespace de4dot.eazworker {
	/// <summary>
	/// 探针要用的那几个 BCL 方法引用。
	///
	/// 这里不直接用 module.Import(typeof(...))：worker 自己的目标框架未必和样本一致
	/// （net10 的 worker 处理 net48 样本是常见情况），Import 会把 System.Console 之类
	/// 映射到 worker 那个运行时的程序集版本，探针跑起来就会找不到。
	/// 样本的 corlib 是 mscorlib 时，一律按名字在 mscorlib 里造引用，与 worker 无关。
	/// </summary>
	sealed class ProbeApi {
		/// <summary>static void System.Console::WriteLine(string)</summary>
		public IMethod WriteLine;
		/// <summary>static System.Text.Encoding System.Text.Encoding::get_Unicode()</summary>
		public IMethod GetUnicode;
		/// <summary>instance byte[] System.Text.Encoding::GetBytes(string)</summary>
		public IMethod GetBytes;
		/// <summary>static string System.Convert::ToBase64String(byte[])</summary>
		public IMethod ToBase64String;
		/// <summary>static string System.String::Concat(string, string)</summary>
		public IMethod Concat;

		public static ProbeApi Create(ModuleDef module) {
			var corlib = module.CorLibTypes.AssemblyRef;
			if (corlib != null && string.Equals(corlib.Name, "mscorlib", StringComparison.OrdinalIgnoreCase))
				return CreateFromCorlib(module);
			return CreateFromRuntime(module);
		}

		static ProbeApi CreateFromCorlib(ModuleDef module) {
			var corlib = module.CorLibTypes.AssemblyRef;
			var stringSig = module.CorLibTypes.String;
			var voidSig = module.CorLibTypes.Void;
			var byteArraySig = new SZArraySig(module.CorLibTypes.Byte);

			var console = new TypeRefUser(module, "System", "Console", corlib);
			var encoding = new TypeRefUser(module, "System.Text", "Encoding", corlib);
			var convert = new TypeRefUser(module, "System", "Convert", corlib);
			var stringType = module.CorLibTypes.String.ToTypeDefOrRef();

			return new ProbeApi {
				WriteLine = new MemberRefUser(module, "WriteLine",
					MethodSig.CreateStatic(voidSig, stringSig), console),
				GetUnicode = new MemberRefUser(module, "get_Unicode",
					MethodSig.CreateStatic(encoding.ToTypeSig()), encoding),
				GetBytes = new MemberRefUser(module, "GetBytes",
					MethodSig.CreateInstance(byteArraySig, stringSig), encoding),
				ToBase64String = new MemberRefUser(module, "ToBase64String",
					MethodSig.CreateStatic(stringSig, byteArraySig), convert),
				Concat = new MemberRefUser(module, "Concat",
					MethodSig.CreateStatic(stringSig, stringSig, stringSig), stringType),
			};
		}

		static ProbeApi CreateFromRuntime(ModuleDef module) {
			return new ProbeApi {
				WriteLine = module.Import(typeof(Console).GetMethod("WriteLine", new[] { typeof(string) })),
				GetUnicode = module.Import(typeof(Encoding).GetProperty("Unicode").GetGetMethod()),
				GetBytes = module.Import(typeof(Encoding).GetMethod("GetBytes", new[] { typeof(string) })),
				ToBase64String = module.Import(typeof(Convert).GetMethod("ToBase64String", new[] { typeof(byte[]) })),
				Concat = module.Import(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })),
			};
		}
	}
}
