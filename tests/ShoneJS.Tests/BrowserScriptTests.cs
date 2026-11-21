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
        Assert.Equal(expected, new Engine().Evaluate(script).ToJsString());

    [Theory]
    [InlineData("let f = (x, x) => x;")]
    [InlineData("let f = async (await) => 1;")]
    [InlineData("let f = (x)\n=> x;")]
    [InlineData("async function f() { function inner() { return await 1; } }")]
    public void RejectsInvalidArrowOrAwaitSyntax(string script) => Assert.Throws<JsSyntaxException>(() => new Engine().ParseScript(script));

    [Fact]
    public void AsyncFunctionsSuspendAndRestoreScopesInFifoOrderOnOwnerThread()
    {
        var engine = new Engine();
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
        var engine = new Engine();
        engine.Evaluate("let obj = { value: 7, async get() { await 0; return this.value; } }; obj.get().then(x => { globalThis.result = x; });");
        engine.DrainJobs();
        Assert.Equal(7, engine.GetGlobal("result").ToNumber());
    }

    [Fact]
    public void AwaitKeepsReferencesAndRunsCatchAndFinallyAcrossSuspensions()
    {
        var engine = new Engine();
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
        var engine = new Engine();
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
        var engine = new Engine();
        var first = Register(engine, "a.js", "import {a} from './b.js'; export function get() { return 4; } globalThis.cycle = a;");
        Register(engine, "b.js", "import {get} from './a.js'; export const a = get();");
        engine.EvaluateModule(first);
        Assert.Equal(4, engine.GetGlobal("cycle").ToNumber());
    }

    [Fact]
    public void ModuleAwaitSuspendsDependentExecutionAndRejectionIsCached()
    {
        var engine = new Engine();
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
        var engine = new Engine();
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
        var engine = new Engine();
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
        var engine = new Engine();
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
    public void ModuleEarlyErrors(string source) => Assert.Throws<JsSyntaxException>(() => new Engine().ParseModule(source));

    [Fact]
    public void ExecutionBudgetsBoundStraightLineCodeAndQueuedJobs()
    {
        var engine = new Engine(new EngineOptions { MaximumExecutionSteps = 20, MaximumPendingJobs = 2 });
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
        var engine = new Engine(new EngineOptions { MaximumExecutionSteps = 50 });
        Assert.Throws<InvalidOperationException>(() => engine.Evaluate(script));
    }

    [Fact]
    public void CancelsParsingExecutionAndPendingMicrotasks()
    {
        using var lifetime = new CancellationTokenSource();
        var engine = new Engine(new EngineOptions { CancellationToken = lifetime.Token });
        using var parse = new CancellationTokenSource();
        parse.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.ParseModule("export const x = 1", parse.Token));
        Assert.Throws<OperationCanceledException>(() => engine.ParseScript("1", parse.Token));
        engine.SetGlobal("cancel", JsValue.From(new JsFunction(_ => { lifetime.Cancel(); return JsValue.Undefined; })));
        engine.EnqueueMicrotask(() => throw new Exception("Canceled job executed"));
        Assert.Throws<OperationCanceledException>(() => engine.Evaluate("try { cancel(); while (true) {} } catch (_) { 1; }"));
        Assert.Throws<OperationCanceledException>(() => engine.DrainJobs());
        Assert.Throws<InvalidOperationException>(() => new Engine(new EngineOptions { MaximumSourceLength = 2 }).ParseScript("123"));
    }

    private static Uri Register(Engine engine, string location, string code)
    {
        var url = new Uri(new Uri("https://fixture.test/"), location);
        engine.RegisterModule(url, engine.ParseModule(code));
        return url;
    }
}
