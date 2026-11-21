// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class BrowserScriptTests
{
    [Theory]
    [InlineData("let f = (a, b,) => a + b; f(2, 3)", "5")]
    [InlineData("let obj = {value: 5, f() { return () => this.value; }}; obj.f()()", "5")]
    [InlineData("let async = x => x + 1; async(4)", "5")]
    [InlineData("let user = {name: 'JS'}; `${user?.name ?? 'missing'}`", "JS")]
    public void SupportsBrowserScriptSyntax(string script, string expected) =>
        Assert.Equal(expected, new JsEngine().Evaluate(script).ToJsString());

    [Theory]
    [InlineData("'a..b.'.split('.').join('|')", "a||b|")]
    [InlineData("'a.b'.split('.', 1).join('|')", "a")]
    [InlineData("'a.b'.split('.', -1).join('|')", "a|b")]
    [InlineData("'a.b'.split('.', 4294967296).length", "0")]
    [InlineData("'ab'.split('').join('|')", "a|b")]
    [InlineData("''.split('').length", "0")]
    [InlineData("''.split(',').length", "1")]
    [InlineData("'a.b'.split()[0]", "a.b")]
    [InlineData("Math.round(2.5)", "3")]
    [InlineData("Math.round(-1.5)", "-1")]
    [InlineData("Math.round(0.49999999999999994)", "0")]
    [InlineData("Math.round(-0.5000000000000001)", "-1")]
    [InlineData("1 / Math.round(-0.5)", "-Infinity")]
    [InlineData("1 / Math.round(-0)", "-Infinity")]
    [InlineData("Math.round({valueOf() { return '2.5'; }})", "3")]
    [InlineData("Math.round()", "NaN")]
    [InlineData("Math.round(Infinity)", "Infinity")]
    [InlineData("(1.25).toFixed(1)", "1.3")]
    [InlineData("(-1.25).toFixed(1)", "-1.3")]
    [InlineData("(2.55).toFixed(1)", "2.5")]
    [InlineData("(1.005).toFixed(2)", "1.00")]
    [InlineData("(-0).toFixed(2)", "0.00")]
    [InlineData("(-0.001).toFixed(2)", "-0.00")]
    [InlineData("new Number(1.25).toFixed('2.9')", "1.25")]
    [InlineData("(1.5).toFixed()", "2")]
    [InlineData("(1.25).toFixed(100).length", "102")]
    [InlineData("(1000000000000000128).toFixed(0)", "1000000000000000128")]
    [InlineData("(1e21).toFixed(2)", "1e+21")]
    [InlineData("NaN.toFixed(2)", "NaN")]
    [InlineData("Infinity.toFixed(2)", "Infinity")]
    [InlineData("new Date(0).toISOString()", "1970-01-01T00:00:00.000Z")]
    [InlineData("new Date(-1.9).toISOString()", "1969-12-31T23:59:59.999Z")]
    [InlineData("new Date('2000-02-29').toISOString()", "2000-02-29T00:00:00.000Z")]
    [InlineData("new Date('2026-11-21T01:02:03.004+01:00').toISOString()", "2026-11-21T00:02:03.004Z")]
    [InlineData("new Date(-8640000000000000).toISOString()", "-271821-04-20T00:00:00.000Z")]
    [InlineData("new Date(8640000000000000).toISOString()", "+275760-09-13T00:00:00.000Z")]
    public void FormatsBrowserNumbersAndDates(string script, string expected) =>
        Assert.Equal(expected, new JsEngine().Evaluate(script).ToJsString());

    [Theory]
    [InlineData("Math.round(1n)", "TypeError")]
    [InlineData("(1).toFixed(101)", "RangeError")]
    [InlineData("NaN.toFixed(-1)", "RangeError")]
    [InlineData("Reflect.apply(Number.prototype.toFixed, '1', [])", "TypeError")]
    [InlineData("new Date('not a date').toISOString()", "RangeError")]
    [InlineData("new Date(8640000000000001).toISOString()", "RangeError")]
    [InlineData("Date.prototype.toISOString()", "TypeError")]
    public void RejectsInvalidBrowserNumberAndDateInputs(string expression, string errorName) =>
        Assert.True(new JsEngine().Evaluate($"try {{ {expression}; false; }} catch (err) {{ err instanceof {errorName}; }}").ToBoolean());

    [Fact]
    public void NewDateWithoutArgumentsFormatsARecentUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var text = new JsEngine().Evaluate("new Date().toISOString()").ToJsString();
        var after = DateTimeOffset.UtcNow;
        var parsed = DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.InRange(parsed, DateTimeOffset.FromUnixTimeMilliseconds(before.ToUnixTimeMilliseconds()), after);
    }

    [Theory]
    [InlineData("let f = (x, x) => x;")]
    [InlineData("let f = async (await) => 1;")]
    [InlineData("let f = (x)\n=> x;")]
    [InlineData("async function f() { function inner() { return await 1; } }")]
    public void RejectsInvalidArrowOrAwaitSyntax(string script) => Assert.Throws<JsSyntaxException>(() => new JsEngine().ParseScript(script));

    [Fact]
    public void AsyncFunctionsSuspendAndRestoreScopesInFifoOrderOnOwnerThread()
    {
        var engine = new JsEngine();
        var promise = engine.CreatePromise(out var resolve, out _);
        engine.SetGlobal("native", promise);
        Assert.Equal("ba", engine.Evaluate("""
            let trace = '';
            async function load() {
                let prefix = 'a';
                trace += prefix;
                {
                    let suffix = 'c';
                    trace += `${await native}${suffix}`;
                }
                return await Promise.resolve(7);
            }
            (async () => { trace += 'b'; return await loadLater(); })().then(x => trace += x);
            function loadLater() { return load(); }
            trace;
            """).ToJsString());
        var owner = Environment.CurrentManagedThreadId;
        var resumed = 0;
        engine.EnqueueMicrotask(() => resumed = Environment.CurrentManagedThreadId);
        resolve(JsValue.From("!"));
        Assert.Equal("ba", engine.Evaluate("trace").ToJsString());
        engine.DrainJobs();
        Assert.Equal(owner, resumed);
        Assert.Equal("ba!c7", engine.Evaluate("trace").ToJsString());
    }

    [Fact]
    public void AsyncMethodKeepsItsReceiverAcrossAwait()
    {
        var engine = new JsEngine();
        engine.Evaluate("let obj = { value: 7, async get() { await 0; return this.value; } }; obj.get().then(x => { globalThis.result = x; });");
        engine.DrainJobs();
        Assert.Equal(7, engine.GetGlobal("result").ToNumber());
    }

    [Fact]
    public void AwaitKeepsReferencesAndRunsCatchAndFinallyAcrossSuspensions()
    {
        var engine = new JsEngine();
        engine.Evaluate("""
            let trace = '';
            let obj = { value: 1 };
            let binding = obj;
            let key = () => (trace += 'k', 'value');
            const run = async function () {
                try {
                    binding[key()] += await Promise.resolve(2);
                    await Promise.reject('r');
                } catch (error) {
                    trace += await error;
                } finally {
                    trace += await 'f';
                }
                return obj.value;
            };
            run().then(v => trace += v);
            binding = { value: 9 };
            Promise.resolve().then(() => trace += 'p');
            """);
        Assert.Equal("k", engine.Evaluate("trace").ToJsString());
        engine.DrainJobs();
        Assert.Equal("kprf3", engine.Evaluate("trace").ToJsString());
    }

    [Fact]
    public void ModulesLinkBeforeEvaluationAndKeepBindingsLiveAcrossReexports()
    {
        var engine = new JsEngine();
        engine.Evaluate("globalThis.log = ''; globalThis.runs = 0;");
        Register(engine, "dep.js", "runs++; export let value = 1; export function inc() { value++; } export default 9;");
        Register(engine, "bridge.js", "export {value as count, default} from './dep.js'; export * from './dep.js';");
        var root = Register(engine, "main.js", """
            import first, {count, inc} from './bridge.js';
            import * as ns from '/dep.js';
            import './dep.js';
            globalThis.log = `${first}:${count}:${ns.value}:${this === undefined}`;
            inc();
            globalThis.log += `:${count}:${ns.value}:${import.meta.url}`;
            export {count};
            """);
        var evaluation = engine.EvaluateModule(root);
        Assert.Same(evaluation.Data, engine.EvaluateModule(root).Data);
        engine.SetGlobal("result", evaluation);
        engine.Evaluate("result.then(ns => { globalThis.resultNs = ns; });");
        engine.DrainJobs();
        Assert.Equal("9:1:1:true:2:2:https://fixture.test/main.js", engine.GetGlobal("log").ToJsString());
        Assert.Equal(1, engine.GetGlobal("runs").ToNumber());
        Assert.Equal(2, engine.Evaluate("resultNs.count").ToNumber());
        Assert.Throws<JsException>(() => engine.Evaluate("'use strict'; resultNs.count = 4;"));
        Assert.Throws<JsException>(() => engine.Evaluate("count"));
    }

    [Fact]
    public void CyclicFunctionsAreLinkedBeforeBodyEvaluation()
    {
        var engine = new JsEngine();
        var first = Register(engine, "a.js", "import {a} from './b.js'; export function get() { return 4; } globalThis.cycle = a;");
        Register(engine, "b.js", "import {get} from './a.js'; export const a = get();");
        engine.EvaluateModule(first);
        Assert.Equal(4, engine.GetGlobal("cycle").ToNumber());
    }

    [Fact]
    public void ModuleAwaitSuspendsDependentExecutionAndRejectionIsCached()
    {
        var engine = new JsEngine();
        var promise = engine.CreatePromise(out _, out var reject);
        engine.SetGlobal("gate", promise);
        engine.SetGlobal("trace", JsValue.From(""));
        Register(engine, "wait.js", "trace += 'd'; export const result = await gate;");
        var root = Register(engine, "root.js", "import {result} from './wait.js'; trace += result;");
        var result = engine.EvaluateModule(root);
        engine.SetGlobal("evaluation", result);
        engine.Evaluate("evaluation.catch(reason => trace += reason);");
        Assert.Equal("d", engine.GetGlobal("trace").ToJsString());
        reject(JsValue.From("x"));
        engine.DrainJobs();
        Assert.Equal("dx", engine.GetGlobal("trace").ToJsString());
        Assert.Same(result.Data, engine.EvaluateModule(root).Data);
    }

    [Fact]
    public void TopLevelAwaitWaitsForHostPromiseBeforeExecutingImporter()
    {
        var engine = new JsEngine();
        var promise = engine.CreatePromise(out var resolve, out _);
        engine.SetGlobal("gate", promise);
        engine.SetGlobal("trace", JsValue.From(""));
        Register(engine, "wait.js", "trace += 'd'; export const result = await gate; trace += 'e';");
        var root = Register(engine, "root.js", "import {result} from './wait.js'; trace += result;");
        engine.EvaluateModule(root);
        resolve(JsValue.From("f"));
        Assert.Equal("d", engine.GetGlobal("trace").ToJsString());
        engine.DrainJobs();
        Assert.Equal("def", engine.GetGlobal("trace").ToJsString());
    }

    [Fact]
    public void RejectedAsyncDependencyDoesNotWaitForAnotherPendingDependency()
    {
        var engine = new JsEngine();
        Register(engine, "pending.js", "await new Promise(() => {});");
        Register(engine, "failed.js", "await Promise.reject('failed');");
        var root = Register(engine, "root.js", "import './pending.js'; import './failed.js'; globalThis.reached = true;");
        engine.SetGlobal("evaluation", engine.EvaluateModule(root));
        engine.Evaluate("evaluation.catch(error => { globalThis.failure = error; });");
        engine.DrainJobs();
        Assert.Equal("failed", engine.GetGlobal("failure").ToJsString());
        Assert.Equal("undefined", engine.Evaluate("typeof reached").ToJsString());
    }

    [Fact]
    public void MissingExportRejectsBeforeAnyModuleBodyRuns()
    {
        var engine = new JsEngine();
        Register(engine, "wrong.js", "globalThis.sawWrong = true; export const present = 1;");
        var root = Register(engine, "root.js", "import {missing} from './wrong.js'; globalThis.sawRoot = true;");
        engine.SetGlobal("evaluation", engine.EvaluateModule(root));
        engine.Evaluate("evaluation.catch(err => { globalThis.failure = err instanceof SyntaxError; });");
        engine.DrainJobs();
        Assert.True(engine.GetGlobal("failure").ToBoolean());
        Assert.Equal("undefined,undefined", engine.Evaluate("typeof sawWrong + ',' + typeof sawRoot").ToJsString());
    }

    [Theory]
    [InlineData("export {x};")]
    [InlineData("export const x = 1; export {x};")]
    [InlineData("import {x} from 'dep'; const x = 1;")]
    [InlineData("if (true) { export const x = 1; }")]
    public void ModuleEarlyErrors(string source) => Assert.Throws<JsSyntaxException>(() => new JsEngine().ParseModule(source));

    [Fact]
    public void ExecutionBudgetsBoundStraightLineCodeAndQueuedJobs()
    {
        var engine = new JsEngine(new JsEngineOptions { MaximumExecutionSteps = 20, MaximumPendingJobs = 2 });
        Assert.Throws<InvalidOperationException>(() => engine.Evaluate(string.Concat(Enumerable.Repeat("1 + 2;", 100))));
        engine.EnqueueMicrotask(() => { });
        engine.EnqueueMicrotask(() => { });
        Assert.Throws<InvalidOperationException>(() => engine.EnqueueMicrotask(() => { }));
        engine.DrainJobs();
        Assert.Equal(3, engine.Evaluate("1+2").ToNumber());
    }

    [Theory]
    [InlineData("new Array(4294967295).join()")]
    [InlineData("new Array(4294967295).toSorted()")]
    [InlineData("JSON.stringify(new Array(4294967295))")]
    [InlineData("new ArrayBuffer(2147483647)")]
    public void NativeHotPathsAlsoConsumeBudget(string script)
    {
        var engine = new JsEngine(new JsEngineOptions { MaximumExecutionSteps = 50 });
        Assert.Throws<InvalidOperationException>(() => engine.Evaluate(script));
    }

    [Fact]
    public void CancelsParsingExecutionAndPendingMicrotasks()
    {
        using var lifetime = new CancellationTokenSource();
        var engine = new JsEngine(new JsEngineOptions { CancellationToken = lifetime.Token });
        using var parse = new CancellationTokenSource();
        parse.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.ParseModule("export const x = 1", parse.Token));
        Assert.Throws<OperationCanceledException>(() => engine.ParseScript("1", parse.Token));
        engine.SetGlobal("cancel", JsValue.From(new JsFunction(_ => { lifetime.Cancel(); return JsValue.Undefined; })));
        engine.EnqueueMicrotask(() => throw new Exception("Canceled job executed"));
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("try { cancel(); while (true) {} } catch (_) { 1; }"));
        Assert.Throws<OperationCanceledException>(() => engine.DrainJobs());
        Assert.Throws<InvalidOperationException>(() => new JsEngine(new JsEngineOptions { MaximumSourceLength = 2 }).ParseScript("123"));
    }

    private static Uri Register(JsEngine engine, string location, string code)
    {
        var url = new Uri(new Uri("https://fixture.test/"), location);
        engine.RegisterModule(url, engine.ParseModule(code));
        return url;
    }
}
