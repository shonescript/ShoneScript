# ShoneScript

ShoneScript 是基于.NET 11 和 C# 编写的脚本解释器，支持高效解析运行 JavaScript (ES2024) 、TypeScript (7.0)、Python (3.15) 脚本。可通过命令行运行脚本，也可以嵌入 .NET 应用，其中 TypeScript 模式直接解析和执行支持范围内的 `.ts` 源码，不运行静态类型检查。

## 快速开始 Javascript

在仓库根目录使用 [.NET 11 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) 运行以下命令。JavaScript 和 TypeScript 模式均可通过 .NET SDK 构建和运行。

```bash
dotnet run --project src/ShoneScript -- -e "const n = 6; n * 7"
```

输出 `42`。直接运行 `dotnet run --project src/ShoneScript` 默认进入 JavaScript 交互模式。每行提交一段脚本，同一会话共享全局绑定，输入 `.exit` 退出。

在 Bash 中可通过标准输入运行完整的多行脚本：

```bash
dotnet run --project src/ShoneScript < script.js
```

单次执行成功时退出码为 `0`，脚本出错时为 `1`，命令用法错误时为 `2`。错误诊断写入标准错误流。运行 `dotnet run --project src/ShoneScript -- --help` 可查看参数。

## 运行 TypeScript

ShoneScript 也支持解析执行 TypeScript，无需安装 Node.js 或外部检查器。解释器直接执行从 TypeScript 源码构建的 AST，无需生成 JavaScript。

```powershell
dotnet run --project src/ShoneScript -- --ts -e 'const twice = (n: number): number => n * 2; twice(21)'
```

输出 `42`。在 Bash 中，还可以运行 `dotnet run --project src/ShoneScript -- --ts < script.ts`。TypeScript 模式以整段输入为单位解析和执行。语法错误会阻止执行，类型注解与值不匹配时仍按运行时语义执行。

## 运行 Python

ShoneScript 还支持解析执行 Python 源码，运行时无需安装 CPython，兼容系统主要标准库，但不支持 CPython 扩展。以下命令输出 `42`。

```bash
dotnet run --project src/ShoneScript -- --py -e 'sum(range(7)) * 2'
```

在 Bash 中可通过 `dotnet run --project src/ShoneScript -- --py < script.py` 执行多行脚本。宿主可引用 `src/ShonePy/ShonePy.csproj`，通过 `ShonePy.PythonEngine` 解析和执行脚本。`PythonOptions.Output` 接收 `print` 的输出。`ShonePy.Interop.JsValueBridge` 显式转换 Python 与 JavaScript 的基础值，Python `int` 转换为 JavaScript `BigInt` 并保留精度。

宿主可通过 `PythonOptions.SearchPaths` 指定纯 Python 模块搜索目录。支持 `.py` 模块、带 `__init__.py` 的包以及相对导入。搜索目录默认为空。

## 嵌入 .NET 应用

给宿主项目添加对 `src/ShoneJs/ShoneJs.csproj` 的项目引用。

```csharp
using ShoneJs;
using ShoneJs.Runtime;

var engine = new Engine();
engine.SetGlobal("baseValue", JsValue.From(40d));
var script = engine.ParseScript("baseValue + 2");
Console.WriteLine(engine.Execute(script)); // 42
```

一次性求值可用 `Engine.Evaluate`。宿主可在作业检查点调用 `Engine.DrainJobs()` 执行待处理的 Promise 作业，CLI 会在每次成功求值后调用它。需要独立的全局环境时，创建单独的 `Engine` 实例。

TypeScript 宿主可使用 `Engine.EvaluateTypeScript`，或分别调用 `ParseTypeScript` 和 `ExecuteTypeScript`。这些入口直接解析并执行支持的 TypeScript 语法，不运行静态类型检查。

## 兼容范围

| 范围 | 当前实现 |
| --- | --- |
| JavaScript | 覆盖常见脚本语法、闭包、属性描述符、稀疏数组、BigInt、BigFloat(扩展)、部分 Promise、Proxy 和 TypedArray 行为，以及 `findLast`、`toSorted` 等 ES2023 数组方法。 |
| TypeScript | 支持单文件脚本，以及变量、参数、返回值类型注解、接口、类型别名、常见泛型、带类型的箭头函数、类型断言、`satisfies`、非空断言和普通枚举。 |
| Python | 面向 Python 3.15 的子集，支持条件表达式、缩进代码块、循环、函数默认参数、仅限位置和仅限关键字参数、`*args`、`**kwargs`、调用参数展开、闭包、`global` 与 `nonlocal` 的基本绑定，以及列表、字典和集合推导式、带星号的解包、Python 3.15 的推导式与生成器表达式展开（PEP 798）、序列切片读取、列表切片赋值、惰性 `range`、可变 `set`（集合运算及常用方法）和不可变 `bytes`（字面量、构造、索引、切片及基本序列运算）。支持内置异常类型的 `raise`、异常链（含 `raise … from`）和 `try` / `except` / `else` / `finally`，以及同步 `with`。支持 Python 类、实例属性、`__init__`、绑定方法、基于 C3 的多重继承、`super()`，以及函数和类装饰器、`staticmethod`、`classmethod` 和 `property`。`match` / `case` 支持字面量、捕获、通配符、序列、映射、用户自定义类、备选模式和守卫条件。同步生成器支持常见控制流中的 `yield`、`yield from` 和 `return`，也支持惰性生成器表达式。类、字典或宿主可以提供 `__enter__` 和 `__exit__`，异常退出时的 traceback 参数为 `None`。托管标准库覆盖部分 `math`、`json`、`re`、`sys`、`functools`、`itertools`、`collections.Counter` 和 `datetime`（日期、无时区的时间和日期时间、时间差）和 `typing`（`cast`、`assert_type`、`assert_never`）。完整语言和核心标准库兼容性仍在开发。支持托管协程的 `async def`、`await`、`async for`、`async with`、`aiter()` 和 `anext()`。支持异步列表、集合和字典推导式，包括混合 `for` 与 `async for` 子句。宿主使用 `PythonEngine.RunCoroutine` 同步驱动协程。事件循环、自定义 awaitable、异步生成器函数及表达式、同步生成器的 `send` / `throw` / `close` 以及通用描述符协议仍待实现。 |
| 待实现或验证 | 完整 ECMAScript 和 TypeScript 7.0 一致性，以及类、模块链接、命名空间、`const enum`、装饰器和 TSX 等能力。 |

### Python 核心标准库覆盖

| 模块 | 已实现的 API | 兼容边界 |
| --- | --- | --- |
| `math` | `pi`、`e`、`tau`、`inf`、`nan`，`sqrt`、`sin`、`cos`、`tan`、`floor`、`ceil`、`trunc`、`fabs`、`log`、`exp`、`pow`、`isfinite`、`isnan`、`gcd`、`lcm`、`isqrt`、`factorial`、`comb`、`perm` | `log` 仅接受单参数。整数运算使用 `Sdcb.Arithmetic.BigInt`。`lcm`、`factorial`、`comb` 和 `perm` 的每轮结果上限为 800 万二进制位。 |
| `json` | `loads`、`dumps` | 支持内置 JSON 值和大整数，调用仅接受一个参数。 |
| `re` | `compile`、`match`、`search`、`fullmatch`、`findall`、`sub` | 支持部分 Python 正则语法和标志，底层使用 .NET 正则并设有超时。 |
| `sys` | `version`、`platform`、`maxsize` | 这些值由 ShonePy 宿主提供。 |
| `functools` | `reduce` | 支持位置或关键字形式的 `initial`。 |
| `itertools` | `chain`、`islice`、`repeat`、`accumulate`、`count` | 托管实现按需读取输入，迭代计入执行预算。 |
| `collections` | `Counter` | 支持常用方法和缺失键返回零。 |
| `datetime` | `date`、`time`、`datetime`、`timedelta`、`MINYEAR`、`MAXYEAR` | 时间和日期时间限于无时区值。 |
| `typing` | `cast`、`assert_type`、`assert_never` | 提供运行时辅助函数。 |

具体行为可查看单元测试和对照用例。JavaScript 的覆盖范围另见固定的 Test262 smoke 清单。

### BigFloat 支持
ShoneJs、TypeScript 和 ShonePy 均接入 Sdcb.Arithmetic.BigFloat，使用项目已有的 Sdcb.Arithmetic 2.0.0 依赖。精度以二进制位计，默认 128 位，可指定 2 至 65,536 位。双目运算采用两个操作数中较高的精度。需要保留原始十进制输入时，请传入字符串，以免输入先经普通浮点数舍入。运算结果超出指数上限时，脚本会收到可捕获的异常。

ShoneJs / TypeScript 默认关闭 BigFloat 支持，宿主可设置 EngineOptions.EnableBigFloat = true，命令行可加 --bigfloat，并可与 --ts 一起使用。

ShonePy 通过 bigfloat 模块按需导入，支持算术运算符、数值比较、平方根和指定精度的 π。

## 构建与验证

```bash
dotnet build ShoneJs.sln -c Release
dotnet test ShoneJs.sln -c Release
dotnet run --project tests/ShoneJs.Test262 -c Release
```

Test262 runner 验证随仓库提交的上游测试和 harness 的 SHA-256，并在独立进程中运行每个变体。当前 smoke 清单包含 111 个变体。

```bash
dotnet run --project tests/ShoneJs.Test262 -c Release -- --list
dotnet run --project tests/ShoneJs.Test262 -c Release -- \
  test/built-ins/Array/prototype/toSorted/immutable.js --mode strict
```

ShonePy 的可选对照测试比较结果表示、标准输出和异常类型。当前对照基准为 CPython `v3.15.0rc3`（构建提交 `8a8eb0b`），对照用例收录在 `tests/ShonePy.Tests/CPythonDifferentialTests.cs`。通用语法也使用 CPython 3.11.11 辅助验证。将 `SHONEPY_CPYTHON` 设为 CPython 可执行文件的绝对路径即可运行。

```bash
SHONEPY_CPYTHON=/absolute/path/to/python dotnet test tests/ShonePy.Tests -c Release --filter FullyQualifiedName~CPythonDifferentialTests
```
验证结果：ShoneJs 450 项通过，ShonePy 610 项通过、4 项 CPython 对照测试跳过，Test262 冒烟测试 111 项全部通过。JS、TS 和 Python 的命令行示例均已实测。

## 项目结构

| 路径 | 用途 |
| --- | --- |
| [`src/ShoneJs`](src/ShoneJs) | 公共宿主 API、Lexer、Parser、AST、解释器和内建对象。 |
| [`src/ShoneScript`](src/ShoneScript) | JavaScript 交互模式，以及 JavaScript、TypeScript 和 Python 的单次执行入口。 |
| [`src/ShonePy`](src/ShonePy) | Python 前端、解释器、宿主 API 和基础值转换。 |
| [`tests/ShonePy.Tests`](tests/ShonePy.Tests) | Python 解释执行及跨语言值转换测试。 |
| [`tests/ShoneJs.Tests`](tests/ShoneJs.Tests) | xUnit 语法、运行时和宿主集成测试。 |
| [`tests/ShoneJs.Test262`](tests/ShoneJs.Test262) | 固定版本的 Test262 smoke 清单、上游语料及 runner。 |

## 许可

ShoneJs 由 Shone 利用 gpt-sol-6.2 编写和修改，采用 MIT 许可证。见 [LICENSE](LICENSE)。

随仓库分发的 Test262 文件遵循其上游许可。见 [Test262 LICENSE](tests/ShoneJs.Test262/upstream/LICENSE)。
