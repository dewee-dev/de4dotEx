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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace de4dot.eazworker {
	/// <summary>
	/// 现代 Eazfuscator.NET 的字符串恢复。
	///
	/// 背景：2021.1+ 的字符串解密器会在运行时检查调用栈（StackTrace / GetCallingAssembly），
	/// 一旦发现是被反射调用的就返回诱饵串，所以"加载样本 + 反射调用解密器"拿不到明文。
	///
	/// 这里的做法是不用反射：往样本里注入
	///   - 每个字符串 id 一个 helper：ldc.i4 &lt;id&gt;; call decrypter; ret
	///   - 一个新的 Main，顺序静态调用这些 helper，把结果写进一个文件
	/// 然后把补丁后的模块改写成 Console exe，作为独立进程跑一次。
	/// 栈上没有任何反射帧，解密器的反调试自然通过。
	///
	/// 代价是必须执行样本（至少是它的模块构造器），所以这一步刻意放在独立进程里，
	/// 由 de4dot 主进程调用，而不是让 de4dot 自己去加载样本。
	/// </summary>
	static class StringRecovery {
		const int DefaultTimeoutSeconds = 120;
		const string ProbeSuffix = ".de4dot-probe";
		const string ErrorMarker = "!error";

		public static int Run(string[] args) {
			if (args.Length < 3) {
				Console.Error.WriteLine("usage: de4dot.eazworker strings <input> <output> [--timeout <seconds>] [--keep]");
				return 2;
			}

			var input = Path.GetFullPath(args[1]);
			var output = Path.GetFullPath(args[2]);
			int timeout = DefaultTimeoutSeconds;
			bool keep = false;

			for (int i = 3; i < args.Length; i++) {
				switch (args[i]) {
				case "--timeout":
					if (i + 1 >= args.Length) {
						Console.Error.WriteLine("--timeout needs a value");
						return 2;
					}
					if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeout) || timeout <= 0) {
						Console.Error.WriteLine("invalid timeout: " + args[i]);
						return 2;
					}
					break;
				case "--keep":
					keep = true;
					break;
				default:
					Console.Error.WriteLine("unknown argument: " + args[i]);
					return 2;
				}
			}

			if (!File.Exists(input)) {
				Console.Error.WriteLine("input not found: " + input);
				return 2;
			}

			Dictionary<int, string> strings;
			string error;
			if (!TryRecover(input, timeout, keep, out strings, out error)) {
				Console.Error.WriteLine("error: " + error);
				return 1;
			}

			WriteMap(output, strings);
			Console.WriteLine("recovered " + strings.Count + " string(s)");
			return 0;
		}

		/// <summary>恢复一张 id → 明文 的表。失败时返回 false 并给出原因，由调用方回退。</summary>
		public static bool TryRecover(string input, int timeoutSeconds, bool keep, out Dictionary<int, string> strings, out string error) {
			strings = new Dictionary<int, string>();
			error = null;

			ModuleDefMD module = null;
			string workRoot = null;
			try {
				module = ModuleDefMD.Load(input);

				var decrypter = FindDecrypter(module);
				if (decrypter == null) {
					error = "no modern Eazfuscator.NET string decrypter found";
					return false;
				}

				var ids = CollectStringIds(module, decrypter);
				if (ids.Count == 0) {
					// 没有调用点，没什么可恢复的；当作成功，输出空表。
					return true;
				}

				var inputDir = Path.GetDirectoryName(input);

				// 探针要作为独立进程跑一次，但样本几乎总会依赖自己目录之外的第三方程序集
				// （典型例子：Gapotchenko.Eazfuscator.NET.dll 依赖 Gapotchenko.FX）。
				// 这些依赖必须在探针进程启动之初就能被 CLR 默认探测找到——样本的 <Module>
				// 模块构造器往往在 Main 之前就加载它们，那时再挂 AssemblyResolve 已经晚了。
				//
				// 做法：把探针和依赖树一起放到一个临时目录，平铺复制所有 *.dll，让 CLR 默认
				// 探测（从 exe 所在目录）就能命中；同时探针里仍然挂了一个 AssemblyResolve
				// 作为兜底，搜索 inputDir 整棵树，处理那些在 Main 之后才被惰性加载的依赖。
				workRoot = CreateWorkRoot();
				var probePath = Path.Combine(workRoot, Path.GetFileNameWithoutExtension(input) + ProbeSuffix + ".exe");

				var entryPoint = InjectProbe(module, decrypter, ids, inputDir);

				// 模块可能是 DLL（没有入口点、PE 头也不允许直接运行）。
				// 改写时统一成 Console exe，这样 Process.Start 能直接跑起来。
				module.Kind = ModuleKind.Console;
				module.EntryPoint = entryPoint;

				var options = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
				using (var fs = File.Create(probePath))
					module.Write(fs, options);

				CopyDependencyDlls(inputDir, workRoot, input);
				CopySidecarFiles(input, probePath);

				string stdout, runError;
				if (!RunProbe(probePath, workRoot, timeoutSeconds, out stdout, out runError)) {
					error = runError;
					return false;
				}

				var wanted = new HashSet<int>(ids);
				int failed = 0;
				foreach (var rawLine in stdout.Split('\n')) {
					var line = rawLine.TrimEnd('\r');
					if (line.Length == 0)
						continue;
					int tab = line.IndexOf('\t');
					if (tab <= 0)
						continue;
					int id;
					if (!int.TryParse(line.Substring(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || !wanted.Contains(id))
						continue;
					var payload = line.Substring(tab + 1);
					if (payload == ErrorMarker) {
						failed++;
						continue;
					}
					try {
						strings[id] = Encoding.Unicode.GetString(Convert.FromBase64String(payload));
					}
					catch (FormatException) {
						failed++;
					}
				}

				if (strings.Count == 0) {
					error = "the probe recovered nothing. Its output was: " + Summarize(stdout);
					return false;
				}

				if (failed > 0)
					Console.Error.WriteLine("warning: " + failed + " string(s) could not be recovered");
				return true;
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
				ex is BadImageFormatException || ex is ModuleWriterException || ex is InvalidOperationException) {
				error = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
			finally {
				if (module != null)
					module.Dispose();
				if (!keep) {
					TryDeleteTree(workRoot);
				}
				else if (workRoot != null) {
					Console.Error.WriteLine("kept probe dir: " + workRoot);
				}
			}
		}

		/// <summary>为探针单独建一个临时工作目录，避免污染样本所在目录。</summary>
		static string CreateWorkRoot() {
			var root = Path.Combine(Path.GetTempPath(), "de4dot-eazworker", Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(root);
			return root;
		}

		/// <summary>
		/// 把样本目录树里的所有 *.dll 平铺复制到探针目录（跳过样本本身）。
		/// 这样 CLR 默认探测就能在探针进程启动时找到第三方依赖，
		/// 而不必等 Main 里的 AssemblyResolve 注册（那时 &lt;Module&gt; 构造器可能已经崩了）。
		/// </summary>
		static void CopyDependencyDlls(string inputDir, string workRoot, string inputFile) {
			if (string.IsNullOrEmpty(inputDir) || !Directory.Exists(inputDir))
				return;
			var inputFullName = Path.GetFullPath(inputFile);
			foreach (var dll in Directory.EnumerateFiles(inputDir, "*.dll", SearchOption.AllDirectories)) {
				var full = Path.GetFullPath(dll);
				if (string.Equals(full, inputFullName, StringComparison.OrdinalIgnoreCase))
					continue;
				var dest = Path.Combine(workRoot, Path.GetFileName(dll));
				try {
					if (!File.Exists(dest))
						File.Copy(full, dest, false);
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
					// 复制失败不致命：这个依赖可能根本用不到，或在别处能被解析到
					Console.Error.WriteLine("warning: can't copy dependency " + dll + ": " + ex.Message);
				}
			}
		}

		/// <summary>
		/// 现代 Eazfuscator 的解密器：internal static string(int)，方法体调用同模块的
		/// string(int,bool)。和 de4dot 侧 ModernEazfuscator 的判定规则保持一致。
		/// </summary>
		static MethodDef FindDecrypter(ModuleDef module) {
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

		static bool IsIntToString(MethodSig sig) =>
			sig != null && sig.Params.Count == 1 &&
			sig.Params[0].ElementType == ElementType.I4 &&
			sig.RetType.ElementType == ElementType.String;

		static bool IsIntBoolToString(MethodSig sig) =>
			sig != null && sig.Params.Count == 2 &&
			sig.Params[0].ElementType == ElementType.I4 &&
			sig.Params[1].ElementType == ElementType.Boolean &&
			sig.RetType.ElementType == ElementType.String;

		static bool CallsIntBoolToString(MethodDef method) {
			foreach (var instr in method.Body.Instructions) {
				if (instr.OpCode.Code != Code.Call && instr.OpCode.Code != Code.Callvirt)
					continue;
				if (instr.Operand is IMethod called && IsIntBoolToString(called.MethodSig))
					return true;
			}
			return false;
		}

		/// <summary>所有 ldc.i4 &lt;id&gt; 紧跟 call 解密器 的调用点参数（去重、保序）。</summary>
		static List<int> CollectStringIds(ModuleDef module, MethodDef decrypter) {
			var ids = new List<int>();
			var seen = new HashSet<int>();
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody || !method.Body.HasInstructions)
						continue;
					var instrs = method.Body.Instructions;
					for (int i = 1; i < instrs.Count; i++) {
						if (!instrs[i - 1].IsLdcI4())
							continue;
						if (!(instrs[i].Operand is IMethod called) || called.FullName != decrypter.FullName)
							continue;
						int id = instrs[i - 1].GetLdcI4Value();
						if (seen.Add(id))
							ids.Add(id);
					}
				}
			}
			return ids;
		}

		/// <summary>
		/// 注入探针类型：每个 id 一个 helper + 一个按顺序调用并写结果的 Main。
		/// 每个调用单独 try/catch，单个字符串失败不影响后面的。
		///
		/// 另外挂一个 AppDomain.AssemblyResolve：样本经常依赖它自己目录之外的第三方程序集
		/// （例如 Gapotchenko.Eazfuscator.NET.dll 依赖 Gapotchenko.FX），探针被改名复制到别处后，
		/// .NET 默认探测找不到这些依赖，模块初始化就抛 FileNotFoundException。解析器从输入目录
		/// 树里递归补加载，让样本能正常执行、字符串能正常解密。
		/// </summary>
		static MethodDef InjectProbe(ModuleDef module, MethodDef decrypter, List<int> ids, string workDir) {
			var probe = new TypeDefUser(UniqueTypeName(module, "De4dotEazProbe"), module.CorLibTypes.Object.TypeDefOrRef);
			probe.Attributes = TypeAttributes.NotPublic | TypeAttributes.Abstract |
				TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit;
			module.Types.Add(probe);

			var api = ProbeApi.Create(module);
			var exception = new TypeRefUser(module, "System", "Exception", module.CorLibTypes.AssemblyRef);

			// 解析器读取的根目录（探针所在的输入目录），运行时递归找依赖。
			var depRoot = new FieldDefUser("<>DepRoot", new FieldSig(module.CorLibTypes.String),
				FieldAttributes.Private | FieldAttributes.Static);
			probe.Fields.Add(depRoot);

			var resolve = BuildResolver(api, depRoot, exception);
			probe.Methods.Add(resolve);

			var main = new MethodDefUser("Main", MethodSig.CreateStatic(module.CorLibTypes.Void),
				MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig);
			main.Body = new CilBody { MaxStack = 8 };

			EmitRegisterResolver(main.Body, api, resolve, depRoot, workDir);

			foreach (var id in ids) {
				var helper = new MethodDefUser("S" + probe.Methods.Count,
					MethodSig.CreateStatic(module.CorLibTypes.String),
					MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig);
				helper.Body = new CilBody { MaxStack = 8 };
				helper.Body.Instructions.Add(Instruction.CreateLdcI4(id));
				helper.Body.Instructions.Add(OpCodes.Call.ToInstruction(decrypter));
				helper.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
				probe.Methods.Add(helper);

				EmitProbeCall(main.Body, api, helper, id, exception);
			}

			main.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
			probe.Methods.Add(main);
			return main;
		}

		/// <summary>
		/// 构造 AssemblyResolve 处理方法：从 DepRoot 目录树递归找同名 .dll 并 LoadFrom。
		/// 整段 try/catch，任何异常都回退到 null（交给默认探测/GAC），不会让模块初始化崩掉。
		/// </summary>
		static MethodDef BuildResolver(ProbeApi api, FieldDef depRoot, ITypeDefOrRef exception) {
			var module = depRoot.DeclaringType.Module;
			var sig = MethodSig.CreateStatic(api.Assembly, module.CorLibTypes.Object, api.ResolveEventArgs);
			var m = new MethodDefUser("<>Resolve", sig,
				MethodAttributes.Static | MethodAttributes.Assembly | MethodAttributes.HideBySig);
			m.Body = new CilBody { MaxStack = 4 };
			m.Body.Variables.Add(new Local(module.CorLibTypes.String));
			m.Body.Variables.Add(new Local(module.CorLibTypes.Int32));
			m.Body.Variables.Add(new Local(new SZArraySig(module.CorLibTypes.String)));

			var instr = m.Body.Instructions;
			var lNoSub = Instruction.Create(OpCodes.Nop);
			var lNull = Instruction.Create(OpCodes.Ldnull);
			var endMarker = Instruction.Create(OpCodes.Nop);

			// name = e.Name; 去掉版本/文化等后缀（第一个逗号之前的部分）
			instr.Add(Instruction.Create(OpCodes.Ldarg_1));
			instr.Add(Instruction.Create(OpCodes.Call, api.ResolveEventArgsGetName));
			instr.Add(Instruction.Create(OpCodes.Stloc_0));
			instr.Add(Instruction.Create(OpCodes.Ldloc_0));
			instr.Add(Instruction.Create(OpCodes.Ldstr, ","));
			instr.Add(Instruction.Create(OpCodes.Call, api.StringIndexOf));
			instr.Add(Instruction.Create(OpCodes.Stloc_1));
			instr.Add(Instruction.Create(OpCodes.Ldloc_1));
			instr.Add(Instruction.Create(OpCodes.Ldc_I4_0));
			instr.Add(Instruction.Create(OpCodes.Blt_S, lNoSub));
			instr.Add(Instruction.Create(OpCodes.Ldloc_0));
			instr.Add(Instruction.Create(OpCodes.Ldc_I4_0));
			instr.Add(Instruction.Create(OpCodes.Ldloc_1));
			instr.Add(Instruction.Create(OpCodes.Call, api.StringSubstring));
			instr.Add(Instruction.Create(OpCodes.Stloc_0));
			// files = Directory.GetFiles(DepRoot, name + ".dll", AllDirectories)
			instr.Add(lNoSub);
			instr.Add(Instruction.Create(OpCodes.Ldsfld, depRoot));
			instr.Add(Instruction.Create(OpCodes.Ldloc_0));
			instr.Add(Instruction.Create(OpCodes.Ldstr, ".dll"));
			instr.Add(Instruction.Create(OpCodes.Call, api.Concat));
			instr.Add(Instruction.Create(OpCodes.Ldc_I4_1));
			instr.Add(Instruction.Create(OpCodes.Call, api.DirectoryGetFiles));
			instr.Add(Instruction.Create(OpCodes.Stloc_2));
			// files == null 或长度为 0 -> 返回 null
			instr.Add(Instruction.Create(OpCodes.Ldloc_2));
			instr.Add(Instruction.Create(OpCodes.Brfalse_S, lNull));
			instr.Add(Instruction.Create(OpCodes.Ldloc_2));
			instr.Add(Instruction.Create(OpCodes.Ldlen));
			instr.Add(Instruction.Create(OpCodes.Ldc_I4_0));
			instr.Add(Instruction.Create(OpCodes.Ble_S, lNull));
			// return Assembly.LoadFrom(files[0])
			instr.Add(Instruction.Create(OpCodes.Ldloc_2));
			instr.Add(Instruction.Create(OpCodes.Ldc_I4_0));
			instr.Add(Instruction.Create(OpCodes.Ldelem_Ref));
			instr.Add(Instruction.Create(OpCodes.Call, api.AssemblyLoadFrom));
			instr.Add(Instruction.Create(OpCodes.Ret));
			// catch: return null
			instr.Add(lNull);
			instr.Add(Instruction.Create(OpCodes.Ret));
			instr.Add(endMarker);

			m.Body.ExceptionHandlers.Add(new ExceptionHandler {
				HandlerType = ExceptionHandlerType.Catch,
				CatchType = exception,
				TryStart = instr[0],
				TryEnd = lNull,
				HandlerStart = lNull,
				HandlerEnd = endMarker,
			});

			return m;
		}

		/// <summary>在 Main 开头设置 DepRoot 并挂上 AssemblyResolve。</summary>
		static void EmitRegisterResolver(CilBody body, ProbeApi api, MethodDef resolve, FieldDef depRoot, string workDir) {
			body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, workDir));
			body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, depRoot));
			body.Instructions.Add(Instruction.Create(OpCodes.Call, api.AppDomainGetCurrentDomain));
			body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
			body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, resolve));
			body.Instructions.Add(Instruction.Create(OpCodes.Newobj, api.ResolveEventHandlerCtor));
			body.Instructions.Add(Instruction.Create(OpCodes.Call, api.AppDomainAddAssemblyResolve));
		}

		static void EmitProbeCall(CilBody body, ProbeApi api, MethodDef helper, int id, ITypeDefOrRef exception) {
			var prefix = id.ToString(CultureInfo.InvariantCulture) + "\t";

			var tryStart = OpCodes.Ldstr.ToInstruction(prefix);
			var handlerStart = OpCodes.Pop.ToInstruction();
			var afterHandler = OpCodes.Nop.ToInstruction();

			// try: prefix + Base64(Unicode(decrypter(id)))
			body.Instructions.Add(tryStart);
			body.Instructions.Add(OpCodes.Call.ToInstruction(api.GetUnicode));
			body.Instructions.Add(OpCodes.Call.ToInstruction(helper));
			body.Instructions.Add(OpCodes.Callvirt.ToInstruction(api.GetBytes));
			body.Instructions.Add(OpCodes.Call.ToInstruction(api.ToBase64String));
			body.Instructions.Add(OpCodes.Call.ToInstruction(api.Concat));
			body.Instructions.Add(OpCodes.Call.ToInstruction(api.WriteLine));
			body.Instructions.Add(OpCodes.Leave.ToInstruction(afterHandler));

			// catch: 记一行 !error，继续下一个
			body.Instructions.Add(handlerStart);
			body.Instructions.Add(OpCodes.Ldstr.ToInstruction(prefix + ErrorMarker));
			body.Instructions.Add(OpCodes.Call.ToInstruction(api.WriteLine));
			body.Instructions.Add(OpCodes.Leave.ToInstruction(afterHandler));

			body.Instructions.Add(afterHandler);

			body.ExceptionHandlers.Add(new ExceptionHandler {
				HandlerType = ExceptionHandlerType.Catch,
				CatchType = exception,
				TryStart = tryStart,
				TryEnd = handlerStart,
				HandlerStart = handlerStart,
				HandlerEnd = afterHandler,
			});
		}

		static string UniqueTypeName(ModuleDef module, string baseName) {
			var name = baseName;
			int suffix = 0;
			while (module.Find(name, false) != null)
				name = baseName + "_" + (++suffix).ToString(CultureInfo.InvariantCulture);
			return name;
		}

		/// <summary>
		/// 配置文件和 runtimeconfig/deps 要跟着探针改名，否则样本跑起来会缺配置。
		/// </summary>
		static void CopySidecarFiles(string input, string probePath) {
			var inputDir = Path.GetDirectoryName(input);
			var inputName = Path.GetFileNameWithoutExtension(input);
			var probeName = Path.GetFileNameWithoutExtension(probePath);

			foreach (var suffix in new[] { ".config", ".runtimeconfig.json", ".deps.json" }) {
				var source = Path.Combine(inputDir, inputName + suffix);
				if (!File.Exists(source))
					continue;
				try {
					File.Copy(source, Path.Combine(Path.GetDirectoryName(probePath), probeName + suffix), true);
				}
				catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
					// 配置文件复制失败不致命，样本可能根本不需要
				}
			}
		}

		static bool RunProbe(string probePath, string workingDir, int timeoutSeconds, out string stdout, out string error) {
			stdout = null;
			error = null;

			var psi = new ProcessStartInfo(probePath) {
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = workingDir,
			};

			using (var process = new Process { StartInfo = psi }) {
				try {
					process.Start();
				}
				catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException) {
					error = "can't start the probe: " + ex.Message;
					return false;
				}

				// 样本的模块构造器可能会 ReadKey / 等输入，直接关掉 stdin
				try {
					process.StandardInput.Close();
				}
				catch (Exception) {
					// 已经退出或没有 stdin，忽略
				}

				var outTask = process.StandardOutput.ReadToEndAsync();
				var errTask = process.StandardError.ReadToEndAsync();

				if (!process.WaitForExit(timeoutSeconds * 1000)) {
					try {
						process.Kill();
					}
					catch (Exception) {
						// 杀不掉也无所谓，下面照样报超时
					}
					error = "the probe didn't finish within " + timeoutSeconds + "s";
					return false;
				}

				process.WaitForExit();
				stdout = outTask.Result + errTask.Result;

				// 探针本身不判断退出码：样本的模块构造器可能以非 0 退出，但结果文件已经写好了。
				return true;
			}
		}

		static string Summarize(string text) {
			if (string.IsNullOrEmpty(text))
				return "(empty)";
			text = text.Trim();
			return text.Length <= 300 ? text : text.Substring(0, 300) + "...";
		}

		static void WriteMap(string output, Dictionary<int, string> strings) {
			var dir = Path.GetDirectoryName(output);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			using (var writer = new StreamWriter(output, false, new UTF8Encoding(false))) {
				writer.WriteLine("# de4dot.eazworker strings v1");
				foreach (var kvp in strings)
					writer.WriteLine(kvp.Key.ToString(CultureInfo.InvariantCulture) + "\t" +
						Convert.ToBase64String(Encoding.Unicode.GetBytes(kvp.Value)));
			}
		}

		static void TryDeleteTree(string path) {
			if (path == null)
				return;
			try {
				if (Directory.Exists(path))
					Directory.Delete(path, true);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
				Console.Error.WriteLine("warning: can't delete " + path + ": " + ex.Message);
			}
		}
	}
}
