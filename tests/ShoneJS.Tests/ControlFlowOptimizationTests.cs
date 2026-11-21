// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class ControlFlowOptimizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinallyOverridesAbruptCompletionsAndKeepsNormalOnes(bool async)
    {
        var engine = new JsEngine();
        var source = """
            let trace = '';
            ASYNC function run() {
                try {
                    try { return AWAIT 'old'; }
                    finally { throw AWAIT 'caught'; }
                } catch (error) {
                    trace += error;
                    try { throw AWAIT 'lost'; }
                    finally { return AWAIT 'new'; }
                } finally { trace += AWAIT 'finally'; }
            }
            """;
        Assert.Equal("caughtfinallynew", RunAndReadTrace(engine, source, async));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopsAndSwitchConsumeOnlyTheirOwnCompletions(bool async)
    {
        var engine = new JsEngine();
        var source = """
            let trace = '';
            ASYNC function run() {
                for (let i = 0; i < 4; i++) {
                    try {
                        switch (i) {
                            case 0: break;
                            case 1: continue;
                            case 2: break;
                            default: return 'unexpected';
                        }
                        trace += 's';
                        if (i === 0) break;
                    } finally {
                        trace += AWAIT i;
                        if (i === 0) continue;
                        if (i === 2) break;
                    }
                }
                do {
                    try { continue; }
                    finally { break; }
                } while (true);
                while (true) { return AWAIT 'done'; }
            }
            """;
        Assert.Equal("s01s2done", RunAndReadTrace(engine, source, async));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalFinallyPreservesPendingThrow(bool async)
    {
        var engine = new JsEngine();
        var source = """
            let trace = '';
            let reason = {};
            ASYNC function run() {
                try {
                    try { throw reason; }
                    finally { trace += AWAIT 'finally'; }
                } catch (error) { return error === reason; }
            }
            """;
        Assert.Equal("finallytrue", RunAndReadTrace(engine, source, async));
    }

    private static string RunAndReadTrace(JsEngine engine, string source, bool async)
    {
        if (async)
        {
            engine.Evaluate(source.Replace("ASYNC", "async").Replace("AWAIT", "await") + "\nrun().then(x => trace += x);");
            engine.DrainJobs();
        }
        else engine.Evaluate(source.Replace("ASYNC", "").Replace("AWAIT", "") + "\nlet result = run(); trace += result;");
        return engine.Evaluate("trace").ToJsString();
    }

    [Theory]
    [InlineData("41; var declaration = 7; function f() {}", "41")]
    [InlineData("try { 41; } finally { 99; }", "41")]
    [InlineData("let i = 0; while (i < 3) { if (++i === 3) break; i; }", "2")]
    [InlineData("switch (1) { case 1: 41; case 2: break; }", "41")]
    public void PreservesScriptCompletionValue(string source, string expected) =>
        Assert.Equal(expected, new JsEngine().Evaluate(source).ToJsString());

    [Fact]
    public void ModuleCompletesWithItsNamespaceAfterSuspendingInFinally()
    {
        var engine = new JsEngine();
        var url = new Uri("https://fixture.test/control-flow.js");
        var module = engine.ParseModule("""
            export let value = '';
            for (let i = 0; i < 3; i++) {
                try { if (i === 0) continue; break; }
                finally { value += await i; }
            }
            42;
            """);
        engine.RegisterModule(url, module);
        engine.SetGlobal("evaluation", engine.EvaluateModule(url));
        engine.Evaluate("evaluation.then(namespace => globalThis.moduleResult = namespace.value);");
        engine.DrainJobs();
        Assert.Equal("01", engine.GetGlobal("moduleResult").ToJsString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoopBudgetFailureBypassesJavaScriptFinally(bool async)
    {
        var engine = new JsEngine(new JsEngineOptions { MaximumLoopIterations = 2 });
        engine.Evaluate("let trace = '';");
        const string body = """
            try { while (true) { continue; } }
            catch (_) { trace += 'catch'; }
            finally { trace += 'finally'; }
            """;
        if (async)
        {
            engine.Evaluate("(async () => { await 0; " + body + " })();");
            Assert.Throws<InvalidOperationException>(() => engine.DrainJobs());
        }
        else Assert.Throws<InvalidOperationException>(() => engine.Evaluate(body));
        Assert.Equal("", engine.Evaluate("trace").ToJsString());
    }

    [Fact]
    public void OperationCancellationBypassesJavaScriptFinallyAndRestoresContext()
    {
        using var cancellation = new CancellationTokenSource();
        var engine = new JsEngine();
        engine.SetGlobal("cancel", JsValue.From(new JsFunction(_ =>
        {
            cancellation.Cancel();
            return JsValue.Undefined;
        })));
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("""
            let trace = '';
            try { cancel(); while (true) break; }
            catch (_) { trace += 'catch'; }
            finally { trace += 'finally'; }
            """, cancellation.Token));
        Assert.Equal("", engine.Evaluate("trace").ToJsString());
    }
}
