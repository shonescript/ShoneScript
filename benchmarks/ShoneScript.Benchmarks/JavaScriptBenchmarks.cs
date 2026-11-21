// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Text;
using BenchmarkDotNet.Attributes;
using ShoneJs;
using ShoneJs.Runtime;

namespace ShoneScript.Benchmarks;

[MemoryDiagnoser]
public class JavaScriptBenchmarks
{
    private const string Call = "add(20, 22)";
    private const int KeyCount = 256;
    private const int ParseStatementCount = 2_000;
    private JsEngine _engine = null!;
    private string _largeSource = null!;
    private JsParsedScript _call = null!;
    private JsParsedScript _cpu = null!;
    private JsParsedScript _strings = null!;
    private JsParsedScript _promises = null!;
    private JsParsedScript _objectKeys = null!;

    [GlobalSetup]
    public void Setup()
    {
        _engine = new JsEngine();
        _engine.Evaluate("function add(a, b) { return a + b; } var promiseResult;");
        _call = _engine.ParseScript(Call);
        _cpu = _engine.ParseScript("""
            (function () {
                let total = 0;
                for (let i = 1; i <= 1000; i++) total += i * i;
                return total;
            })();
            """);
        _strings = _engine.ParseScript("""
            (function () {
                let words = 'red, green, blue, green'.split(', ');
                return words.join('|').replaceAll('green', 'lime').padEnd(20, '.');
            })();
            """);
        _promises = _engine.ParseScript("""
            (function () {
                promiseResult = undefined;
                let chain = Promise.resolve(0);
                function increment(value) { return value + 1; }
                for (let i = 0; i < 32; i++) chain = chain.then(increment);
                chain.then(function (value) { promiseResult = value; });
            })();
            """);

        var largeSource = new StringBuilder("(function () { let total = 0;\n");
        for (int i = 1; i <= ParseStatementCount; i++)
            largeSource.AppendLine($"total += {{ section: 'entry-{i}', amount: {i} }}.amount;");
        _largeSource = largeSource.Append("return total; })();").ToString();

        _engine.Evaluate($$"""
            var records = {};
            for (let i = {{KeyCount / 2}} - 1; i >= 0; i--) {
                records['item-' + i] = i;
                records[i] = i;
            }
            """);
        _objectKeys = _engine.ParseScript("Object.keys(records)");

        if (ParseAndExecuteExpression().ToNumber() != 7 || ParseAndExecuteCall().ToNumber() != 42 ||
            ExecuteParsedCall().ToNumber() != 42 ||
            CpuLoop().ToNumber() != 333_833_500 || StringOperations().ToJsString() != "red|lime|blue|lime.." ||
            PromiseChain().ToNumber() != 32 ||
            _engine.Execute(ParseLargeScript()).ToNumber() != ParseStatementCount * (ParseStatementCount + 1) / 2 ||
            ObjectKeys().Data is not JsArray keys || keys.Length != KeyCount || keys.Get("0").ToJsString() != "0" ||
            keys.Get((KeyCount - 1).ToString()).ToJsString() != "item-0")
            throw new InvalidOperationException("A JavaScript benchmark returned an unexpected result.");
    }

    [Benchmark]
    public JsValue ParseAndExecuteExpression() => _engine.Evaluate("1 + 2 * 3");

    [Benchmark]
    public JsValue ParseAndExecuteCall() => _engine.Evaluate(Call);

    [Benchmark]
    public JsValue ExecuteParsedCall() => _engine.Execute(_call);

    [Benchmark]
    public JsParsedScript ParseLargeScript() => _engine.ParseScript(_largeSource);

    [Benchmark]
    public JsValue ObjectKeys() => _engine.Execute(_objectKeys);

    [Benchmark]
    public JsValue CpuLoop() => _engine.Execute(_cpu);

    [Benchmark]
    public JsValue StringOperations() => _engine.Execute(_strings);

    [Benchmark]
    public JsValue PromiseChain()
    {
        _engine.Execute(_promises);
        _engine.DrainJobs();
        return _engine.GetGlobal("promiseResult");
    }
}
