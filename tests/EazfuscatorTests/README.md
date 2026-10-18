# Eazfuscator.NET 回归测试

覆盖本仓库对**现代 Eazfuscator.NET（2021.1+）**的支持：识别规则（`ModernEazfuscator`）
和外部解包器委托链路（`EazUnpackerBridge`）。

夹具都是普通 C# 源码，用 `dotnet build` 现编，**不需要 ilasm/ildasm，也不需要装
Eazfuscator**。原因很简单：被测的是"IL 形状"，不是 Eazfuscator 的输出，用真实混淆
样本反而会把商业工具的行为绑进测试里。

## 跑法

```powershell
# Windows（和发布的 net48 产物一致）
dotnet build -c Release -p:TargetFrameworks=net48 tests/EazfuscatorTests/EazfuscatorTests.csproj
tests\EazfuscatorTests\bin\Release\net48\EazfuscatorTests.exe
```

```bash
# Linux / macOS
dotnet build -c Release -p:TargetFrameworks=net10.0 tests/EazfuscatorTests/EazfuscatorTests.csproj
dotnet tests/EazfuscatorTests/bin/Release/net10.0/EazfuscatorTests.dll
```

`--de4dot <path>` 指定被测的 de4dot；不指定时按 `Release/net48` → `Release/net10.0`
→ `publish-net10.0` 的顺序在仓库里找。

其它参数：`--filter <text>` 只跑名字匹配的场景，`--keep` 保留临时目录，`-h` 看用法。
退出码 `0` 全过（跳过不算失败）、`1` 有失败、`2` 用法/环境错误。

> 编译时用 `-p:TargetFrameworks=xxx` 而不是 `-f xxx`：本工程是多目标，
> `-f` 只影响 build，还原阶段仍会尝试两个目标框架。

## 场景

| 场景 | 断言 | 平台 |
|---|---|---|
| `modern-detected` | 正例夹具被判为 `Eazfuscator.NET (modern, 2021.1+)` | 全部 |
| `lookalike-not-detected` | 两个反例都不出现 `Eazfuscator.NET` | 全部 |
| `bridge-replaces-module` | 输出里是解包产物的标记类型，原始夹具的类型消失 | Windows |
| `bridge-disabled-falls-back` | `--no-eaz-unpack` 时告警并仍输出原模块 | Windows |
| `bridge-incomplete-unpacker-falls-back` | 解包器缺 `worker\` 时告警、不崩、仍输出 | Windows |
| `help-lists-options` | `-h` 里有 `--eaz-unpacker` 等四个选项 | 全部 |

需要 Windows 的场景是因为桩解包器是 net48 的 exe；非 Windows 平台会打印 `SKIP`
而不是失败。

## 夹具

| 目录 | 作用 |
|---|---|
| `fixtures/ModernStringDecrypter` | 正例：`internal static string(int)` 调用 `string(int,bool)` |
| `fixtures/LookalikeStringMethod` | 反例：被调用方是 `string(int)`；以及 `public` 而非 `internal` |
| `fixtures/UnpackedMarker` | 桩解包器交出的"解包产物"，用来判断模块是否真的被替换 |
| `fixtures/StubUnpacker` | 假 `Unpacker-net48.exe`，命令行契约与真实的一致 |

## 关于变异验证

这几个断言不是"跑通就算"，改坏实现会真的红：

- 去掉 `FindStringDecrypter` 里的 `method.IsAssembly` 检查 →
  `lookalike-not-detected` 失败；
- 让 `GetDecryptedModule` 返回 `false`（即不再换模块）→
  `bridge-replaces-module` 失败。

改动识别规则或委托逻辑后，建议按上面两条各做一次反向验证，确认测试还在守东西。
