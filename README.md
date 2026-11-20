# ShoneJS

ShoneJS 是一个用 C# 编写的 JavaScript 和 TypeScript 解释器，面向 .NET 11。可通过命令行运行脚本，也可以嵌入 .NET 应用。其中 TypeScript 模式直接解析和执行支持范围内的 `.ts` 源码，并在执行前完成静态类型检查。

## 快速开始

在仓库根目录使用 [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) 运行以下命令。JavaScript 模式只需要 .NET SDK。

```bash
dotnet run --project src/ShoneJS.Cli -- -e "const n = 6; n * 7"
```

输出 `42`。直接运行 `dotnet run --project src/ShoneJS.Cli` 可进入 JavaScript 交互模式。每行提交一段脚本，同一会话共享全局绑定，输入 `.exit` 退出。

在 Bash 中可通过标准输入运行完整的多行脚本：

```bash
dotnet run --project src/ShoneJS.Cli < script.js
```

单次执行成功时退出码为 `0`，脚本出错时为 `1`，命令用法错误时为 `2`。错误诊断写入标准错误流。运行 `dotnet run --project src/ShoneJS.Cli -- --help` 可查看参数。

## 运行 TypeScript

TypeScript 模式需要官方 TypeScript **7.0.2** 原生检查器。可使用 npm 安装 `typescript@7.0.2`，并将 `SHONEJS_TSGO` 指向对应平台包中的原生 `tsc` 或 `tsc.exe`。以下示例适用于 Windows x64 和 PowerShell。

```powershell
$checkerDir = Join-Path ([System.IO.Path]::GetTempPath()) 'shonejs-ts-checker'
npm install --prefix $checkerDir typescript@7.0.2
$env:SHONEJS_TSGO = Join-Path $checkerDir 'node_modules/@typescript/typescript-win32-x64/lib/tsc.exe'
& $env:SHONEJS_TSGO --version
dotnet run --project src/ShoneJS.Cli -- --ts -e 'const twice = (n: number): number => n * 2; twice(21)'
```

输出 `42`。在 Bash 中配置同一环境变量后，还可以运行 `dotnet run --project src/ShoneJS.Cli -- --ts < script.ts`。TypeScript 模式以整段输入为单位检查和执行。

ShoneJS 将原文解析为带源码位置的 AST，并用官方检查器执行 `--strict --noEmit --target es2023` 检查。检查通过后，C# 解释器直接执行 AST。有类型错误、检查器缺失或版本不匹配时，整段脚本停止在执行前。比如 `let n: number = 'x';` 会报告 `TS2322`，并附上行列位置。

## 嵌入 .NET 应用

给宿主项目添加对 `src/ShoneJS/ShoneJS.csproj` 的项目引用。

```csharp
using ShoneJS;
using ShoneJS.Runtime;

var engine = new Engine();
engine.SetGlobal("baseValue", JsValue.From(40d));
var script = engine.ParseScript("baseValue + 2");
Console.WriteLine(engine.Execute(script)); // 42
```

一次性求值可用 `Engine.Evaluate`。宿主可在作业检查点调用 `Engine.DrainJobs()` 执行待处理的 Promise 作业，CLI 会在每次成功求值后调用它。需要独立的全局环境时，创建单独的 `Engine` 实例。

TypeScript 宿主通过 `EngineOptions.TypeScript.ToolPath` 配置检查器，并使用 `Engine.EvaluateTypeScript`，或分别调用 `ParseTypeScript` 和 `ExecuteTypeScript`。检查失败会抛出 `TypeScriptCheckException`，其 `Diagnostics` 提供结构化诊断。

## 兼容范围

| 范围 | 当前实现 |
| --- | --- |
| JavaScript | 覆盖常见脚本语法、闭包、属性描述符、稀疏数组、BigInt、部分 Promise、Proxy 和 TypedArray 行为，以及 `findLast`、`toSorted` 等 ES2023 数组方法。 |
| TypeScript | 支持单文件脚本，以及变量、参数、返回值类型注解、接口、类型别名、常见泛型、带类型的箭头函数、类型断言、`satisfies`、非空断言和普通枚举。 |
| 待实现或验证 | 完整 ECMAScript 和 TypeScript 7.0 一致性，以及类、模块链接、命名空间、`const enum`、装饰器和 TSX 等能力。 |

详细覆盖范围可查看单元测试和固定的 Test262 smoke 清单。BigInt 通过 `Sdcb.Arithmetic.BigInt` 实现。

## 构建与验证

```bash
dotnet build ShoneJS.sln -c Release
dotnet test ShoneJS.sln -c Release
dotnet run --project tests/ShoneJS.Test262 -c Release
```

使用官方检查器的集成用例需要在运行 `dotnet test` 前设置 `SHONEJS_TSGO`。Test262 runner 验证随仓库提交的上游测试和 harness 的 SHA-256，并在独立进程中运行每个变体。当前 smoke 清单包含 111 个变体。

```bash
dotnet run --project tests/ShoneJS.Test262 -c Release -- --list
dotnet run --project tests/ShoneJS.Test262 -c Release -- \
  test/built-ins/Array/prototype/toSorted/immutable.js --mode strict
```

## 项目结构

| 路径 | 用途 |
| --- | --- |
| [`src/ShoneJS`](src/ShoneJS) | 公共宿主 API、Lexer、Parser、AST、解释器、内建对象和 TypeScript 检查器适配。 |
| [`src/ShoneJS.Cli`](src/ShoneJS.Cli) | JavaScript 交互模式，以及 JavaScript 和 TypeScript 的单次执行入口。 |
| [`tests/ShoneJS.Tests`](tests/ShoneJS.Tests) | xUnit 语法、运行时和宿主集成测试。 |
| [`tests/ShoneJS.Test262`](tests/ShoneJS.Test262) | 固定版本的 Test262 smoke 清单、上游语料及 runner。 |

## 许可

项目根目录尚未添加许可证。仓库中随附的 Test262 文件适用上游许可。见 [Test262 LICENSE](tests/ShoneJS.Test262/upstream/LICENSE)。
