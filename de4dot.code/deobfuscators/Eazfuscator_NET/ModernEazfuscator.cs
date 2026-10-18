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
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.Eazfuscator_NET {
	/// <summary>
	/// 现代 Eazfuscator.NET（2021.1 及以后）的识别。
	///
	/// 本模块原有的 ScanForObfuscator() 只认 1.1–2.5 的特征，2021 之后的版本
	/// 在那里找不到任何东西，整份文件会被判定成 Unknown。这里的判定规则移植自
	/// EazUnpacker 内置的 EazFixer：现代版本的字符串解密器是 internal static
	/// string(int)，并且方法体里会调用同一模块内的 string(int,bool) 重载。
	/// 这个形状比"某个资源叫 Eazfuscator"之类的标记稳定得多，也不依赖具体版本号。
	/// </summary>
	sealed class ModernEazfuscator {
		/// <summary>仅凭字符串解密器形状就能判定的分数。</summary>
		public const int PrimaryScore = 60;
		/// <summary>达到该分数即认为是现代 Eazfuscator。</summary>
		public const int Threshold = 60;

		public bool Detected => score >= Threshold;
		public int Score => score;
		public string Evidence => evidence;
		public MethodDef StringDecrypter => stringDecrypter;

		int score;
		string evidence;
		MethodDef stringDecrypter;

		public void Find(ModuleDefMD module) {
			score = 0;
			evidence = null;
			stringDecrypter = null;
			if (module == null)
				return;

			var decrypter = FindStringDecrypter(module);
			if (decrypter != null) {
				stringDecrypter = decrypter;
				score += PrimaryScore;
				evidence = $"string decrypter {decrypter.FullName} calls string(int,bool)";
			}

			// 以下只作为佐证提高置信度，单独出现不足以判定。
			if (HasEazfuscatorAttribute(module)) {
				score += 40;
				evidence = Append(evidence, "Eazfuscator custom attribute");
			}
			if (HasEazfuscatorResource(module)) {
				score += 20;
				evidence = Append(evidence, "Eazfuscator resource");
			}
			if (HasLegacyNamePattern(module)) {
				score += 10;
				evidence = Append(evidence, "dje_*_ejd names");
			}
		}

		static string Append(string text, string item) =>
			text == null ? item : text + "; " + item;

		/// <summary>internal static string(int)，且方法体调用 string(int,bool)。</summary>
		static MethodDef FindStringDecrypter(ModuleDefMD module) {
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.IsStatic || !method.IsAssembly)
						continue;
					if (method.IsAbstract || !method.HasBody || !method.Body.HasInstructions)
						continue;
					if (!IsIntToString(method.MethodSig))
						continue;
					if (!CallsIntBoolToString(method))
						continue;
					return method;
				}
			}
			return null;
		}

		static bool CallsIntBoolToString(MethodDef method) {
			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
					continue;
				if (instr.Operand is IMethod called && IsIntBoolToString(called.MethodSig))
					return true;
			}
			return false;
		}

		static bool IsIntToString(MethodSig sig) =>
			sig != null &&
			sig.Params.Count == 1 &&
			sig.Params[0].ElementType == ElementType.I4 &&
			sig.RetType.ElementType == ElementType.String;

		static bool IsIntBoolToString(MethodSig sig) =>
			sig != null &&
			sig.Params.Count == 2 &&
			sig.Params[0].ElementType == ElementType.I4 &&
			sig.Params[1].ElementType == ElementType.Boolean &&
			sig.RetType.ElementType == ElementType.String;

		static bool HasEazfuscatorAttribute(ModuleDefMD module) {
			var asm = module.Assembly;
			if (asm == null)
				return false;
			foreach (var attr in asm.CustomAttributes) {
				if (ContainsEazfuscator(attr.TypeFullName))
					return true;
			}
			return false;
		}

		static bool HasEazfuscatorResource(ModuleDefMD module) {
			foreach (var resource in module.Resources) {
				if (ContainsEazfuscator(resource.Name))
					return true;
			}
			return false;
		}

		static bool ContainsEazfuscator(string name) =>
			!string.IsNullOrEmpty(name) && name.IndexOf("eazfuscator", StringComparison.OrdinalIgnoreCase) >= 0;

		/// <summary>老版本会生成 dje_xxx_ejd 这样的名字，现代版本仍可能保留一部分。</summary>
		static bool HasLegacyNamePattern(ModuleDefMD module) {
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					var name = method.Name.String;
					if (name != null && name.Length > 8 &&
						name.StartsWith("dje_", StringComparison.Ordinal) &&
						name.EndsWith("_ejd", StringComparison.Ordinal))
						return true;
				}
			}
			return false;
		}
	}
}
