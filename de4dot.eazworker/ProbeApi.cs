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
using System.Reflection;
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
		/// <summary>instance int32 System.String::IndexOf(string)</summary>
		public IMethod StringIndexOf;
		/// <summary>instance string System.String::Substring(int32, int32)</summary>
		public IMethod StringSubstring;
		/// <summary>static string[] System.IO.Directory::GetFiles(string, string, System.IO.SearchOption)</summary>
		public IMethod DirectoryGetFiles;
		/// <summary>static System.Reflection.Assembly System.Reflection.Assembly::LoadFrom(string)</summary>
		public IMethod AssemblyLoadFrom;
		/// <summary>instance string System.Reflection.ResolveEventArgs::get_Name()</summary>
		public IMethod ResolveEventArgsGetName;
		/// <summary>static System.AppDomain System.AppDomain::get_CurrentDomain()</summary>
		public IMethod AppDomainGetCurrentDomain;
		/// <summary>instance void System.AppDomain::add_AssemblyResolve(System.ResolveEventHandler)</summary>
		public IMethod AppDomainAddAssemblyResolve;
		/// <summary>instance void System.ResolveEventHandler::.ctor(object, intptr)</summary>
		public IMethod ResolveEventHandlerCtor;
		/// <summary>System.Reflection.Assembly 的 TypeSig（用作解析方法的返回类型）。</summary>
		public TypeSig Assembly;
		/// <summary>System.Reflection.ResolveEventArgs 的 TypeSig（用作解析方法的参数类型）。</summary>
		public TypeSig ResolveEventArgs;

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
			var stringArraySig = new SZArraySig(stringSig);

			var console = new TypeRefUser(module, "System", "Console", corlib);
			var encoding = new TypeRefUser(module, "System.Text", "Encoding", corlib);
			var convert = new TypeRefUser(module, "System", "Convert", corlib);
			var stringType = module.CorLibTypes.String.ToTypeDefOrRef();
			var directory = new TypeRefUser(module, "System.IO", "Directory", corlib);
			var searchOption = new TypeRefUser(module, "System.IO", "SearchOption", corlib);
			var assembly = new TypeRefUser(module, "System.Reflection", "Assembly", corlib);
			var resolveEventArgs = new TypeRefUser(module, "System.Reflection", "ResolveEventArgs", corlib);
			var appDomain = new TypeRefUser(module, "System", "AppDomain", corlib);
			var resolveEventHandler = new TypeRefUser(module, "System", "ResolveEventHandler", corlib);

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
				StringIndexOf = new MemberRefUser(module, "IndexOf",
					MethodSig.CreateInstance(module.CorLibTypes.Int32, stringSig), stringType),
				StringSubstring = new MemberRefUser(module, "Substring",
					MethodSig.CreateInstance(stringSig, module.CorLibTypes.Int32, module.CorLibTypes.Int32), stringType),
				DirectoryGetFiles = new MemberRefUser(module, "GetFiles",
					MethodSig.CreateStatic(stringArraySig, stringSig, stringSig, searchOption.ToTypeSig()), directory),
				AssemblyLoadFrom = new MemberRefUser(module, "LoadFrom",
					MethodSig.CreateStatic(assembly.ToTypeSig(), stringSig), assembly),
				ResolveEventArgsGetName = new MemberRefUser(module, "get_Name",
					MethodSig.CreateInstance(stringSig), resolveEventArgs),
				AppDomainGetCurrentDomain = new MemberRefUser(module, "get_CurrentDomain",
					MethodSig.CreateStatic(appDomain.ToTypeSig()), appDomain),
				AppDomainAddAssemblyResolve = new MemberRefUser(module, "add_AssemblyResolve",
					MethodSig.CreateInstance(voidSig, resolveEventHandler.ToTypeSig()), appDomain),
				ResolveEventHandlerCtor = new MemberRefUser(module, ".ctor",
					MethodSig.CreateInstance(voidSig, module.CorLibTypes.Object, module.CorLibTypes.IntPtr), resolveEventHandler),
				Assembly = assembly.ToTypeSig(),
				ResolveEventArgs = resolveEventArgs.ToTypeSig(),
			};
		}

		static ProbeApi CreateFromRuntime(ModuleDef module) {
			return new ProbeApi {
				WriteLine = module.Import(typeof(Console).GetMethod("WriteLine", new[] { typeof(string) })),
				GetUnicode = module.Import(typeof(Encoding).GetProperty("Unicode").GetGetMethod()),
				GetBytes = module.Import(typeof(Encoding).GetMethod("GetBytes", new[] { typeof(string) })),
				ToBase64String = module.Import(typeof(Convert).GetMethod("ToBase64String", new[] { typeof(byte[]) })),
				Concat = module.Import(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })),
				StringIndexOf = module.Import(typeof(string).GetMethod("IndexOf", new[] { typeof(string) })),
				StringSubstring = module.Import(typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) })),
				DirectoryGetFiles = module.Import(typeof(System.IO.Directory).GetMethod("GetFiles", new[] { typeof(string), typeof(string), typeof(System.IO.SearchOption) })),
				AssemblyLoadFrom = module.Import(typeof(Assembly).GetMethod("LoadFrom", new[] { typeof(string) })),
				ResolveEventArgsGetName = module.Import(typeof(ResolveEventArgs).GetProperty("Name").GetGetMethod()),
				AppDomainGetCurrentDomain = module.Import(typeof(AppDomain).GetProperty("CurrentDomain").GetGetMethod()),
				AppDomainAddAssemblyResolve = module.Import(typeof(AppDomain).GetEvent("AssemblyResolve").GetAddMethod()),
				ResolveEventHandlerCtor = module.Import(typeof(ResolveEventHandler).GetConstructor(new[] { typeof(object), typeof(IntPtr) })),
				Assembly = module.Import(typeof(Assembly)).ToTypeSig(),
				ResolveEventArgs = module.Import(typeof(ResolveEventArgs)).ToTypeSig(),
			};
		}
	}
}
