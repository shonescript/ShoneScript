// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJS.Runtime;

namespace ShoneJS.Tests;

public class PromiseTests
{
    [Fact]
    public void ExecutorIsSynchronousAndReactionsRunInFifoOrderAtCheckpoint()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            let log = '';
            let promise = new Promise(function (resolve, reject) {
                log += 'a';
                resolve(3);
                reject(5);
                throw 9;
            });
            promise.then(function (value) { log += value; return 4; })
                .then(function (value) { log += value; });
            promise.then(function (value) { log += 'c'; });
            log += 'b';
            log;
            """);
        Assert.Equal("ab", result.ToJsString());
        Assert.Equal("ab", engine.Evaluate("log").ToJsString());
        engine.DrainJobs();
        Assert.Equal("ab3c4", engine.Evaluate("log").ToJsString());
    }

    [Fact]
    public void ResolutionGetsThenSynchronouslyAndInvokesItInAJob()
    {
        var engine = new Engine();
        var result = engine.Evaluate("""
            let log = '';
            let thenable = { value: 7 };
            Object.defineProperty(thenable, 'then', { get: function () {
                log += 'g';
                return function (resolve, reject) {
                    log += 't';
                    resolve(this.value);
                    reject(9);
                    throw 10;
                };
            } });
            let promise = Promise.resolve(thenable);
            promise.then(function (value) { log += value; });
            log += 's';
            log;
            """);
        Assert.Equal("gs", result.ToJsString());
        engine.DrainJobs();
        Assert.Equal("gst7", engine.Evaluate("log").ToJsString());
    }

    [Theory]
    [InlineData("Promise.resolve(1).then(function () { throw 'handler'; }).catch(record);", "handler")]
    [InlineData("new Promise(function () { throw 'executor'; }).then(null, null).catch(record);", "executor")]
    [InlineData("let t = {}; Object.defineProperty(t, 'then', {get: function () { throw 'getter'; }}); Promise.resolve(t).catch(record);", "getter")]
    [InlineData("let p = Promise.resolve().then(function () { return p; }); p.catch(function (e) { record(e instanceof TypeError); });", "true")]
    [InlineData("let resolve; let p = new Promise(function (r) { resolve = r; }); p.catch(function (e) { record(e instanceof TypeError); }); resolve(p);", "true")]
    [InlineData("let s = Symbol(); new Promise(function () { throw s; }).catch(function (e) { record(e === s); });", "true")]
    [InlineData("let p = Promise.resolve(3); record(Promise.resolve(p) === p);", "true")]
    [InlineData("Promise.resolve(9).then(5).then(record);", "9")]
    [InlineData("let resolve; let reject; let adopted = new Promise(function (complete) { Promise.resolve().then(function () { complete('adopted'); }); }); let outer = new Promise(function (r, j) { resolve = r; reject = j; }); outer.then(record, record); resolve(adopted); reject('ignored');", "adopted")]
    public void PropagatesValuesAndRejections(string script, string expected) => Assert.Equal(expected, RunRecorded(script));

    [Theory]
    [InlineData("Promise.resolve('yes').finally(function () { return Promise.resolve('ignored'); }).then(record);", "yes")]
    [InlineData("Promise.reject('before').finally(function () { return Promise.resolve(); }).catch(record);", "before")]
    [InlineData("Promise.reject('before').finally(function () { return Promise.reject('after'); }).catch(record);", "after")]
    [InlineData("Promise.resolve('before').finally(function () { throw 'after'; }).catch(record);", "after")]
    [InlineData("Promise.resolve('before').finally(12).then(record);", "before")]
    [InlineData("let finish; Promise.resolve('value').finally(function (arg) { record(arg === undefined); return new Promise(function (resolve) { finish = resolve; }); }).then(record); Promise.resolve().then(function () { record('waiting'); finish(); });", "truewaitingvalue")]
    public void FinallyWaitsAndPreservesTheOriginalCompletionUnlessCleanupFails(string script, string expected)
        => Assert.Equal(expected, RunRecorded(script));

    [Theory]
    [InlineData("let complete; let slow = new Promise(function (resolve) { complete = resolve; }); Promise.all([slow, Promise.resolve(2)]).then(function (result) { record(result[0] + ',' + result[1]); }); complete(1);", "1,2")]
    [InlineData("Promise.all([Promise.resolve(1), Promise.reject('bad')]).catch(record);", "bad")]
    [InlineData("Promise.all([]).then(function (result) { record(result.length); });", "0")]
    [InlineData("Promise.allSettled([]).then(function (result) { record(result.length); });", "0")]
    [InlineData("Promise.all('A😀').then(function (result) { record(result.length + ',' + result[1]); });", "2,😀")]
    [InlineData("Promise.all({0: 1, length: 1}).catch(function (e) { record(e instanceof TypeError); });", "true")]
    [InlineData("Promise.allSettled([1, Promise.reject('bad')]).then(function (r) { record(r[0].status + ':' + r[0].value + ',' + r[1].status + ':' + r[1].reason); });", "fulfilled:1,rejected:bad")]
    [InlineData("Promise.race([Promise.resolve('first'), Promise.reject('second')]).then(record);", "first")]
    [InlineData("Promise.race([new Promise(function () {}), Promise.reject('first')]).catch(record);", "first")]
    [InlineData("Promise.race([]).then(record, record);", "")]
    [InlineData("let original = Promise.resolve; let reads = 0; Object.defineProperty(Promise, 'resolve', { get: function () { reads += 1; return original; } }); Promise.all([1, 2]).then(function () { record(reads); });", "1")]
    [InlineData("Promise.resolve = function () { return { then: function (ok, fail) { ok('once'); fail('twice'); } }; }; Promise.allSettled([1]).then(function (r) { record(r[0].value); });", "once")]
    public void CombinatorsSettleAndKeepInputOrder(string script, string expected) => Assert.Equal(expected, RunRecorded(script));

    [Theory]
    [InlineData("Promise(function () {});")]
    [InlineData("new Promise();")]
    [InlineData("Promise.prototype.then(function () {});")]
    [InlineData("Promise.prototype.finally(function () {});")]
    [InlineData("let f = Promise.resolve; f(1);")]
    public void RejectsInvalidCallsSynchronously(string script)
    {
        var error = Assert.Throws<JsException>(() => new Engine().Evaluate(script));
        Assert.Equal("TypeError", error.ErrorName);
    }

    [Fact]
    public void CanBorrowStaticResolveWithAnotherConstructor()
    {
        Assert.Equal("5", new Engine().Evaluate("""
            function Custom(executor) {
                let result = this;
                executor(function (value) { result.value = value; }, function () {});
            }
            Custom.resolve = Promise.resolve;
            let custom = Custom.resolve(5);
            (custom instanceof Custom) && custom.value;
            """).ToJsString());
    }

    [Fact]
    public void PendingPromisesCanSettleInALaterScript()
    {
        var engine = new Engine();
        engine.Evaluate("let release; let result; new Promise(function (resolve) { release = resolve; }).then(function (x) { result = x; });");
        engine.DrainJobs();
        Assert.Equal(JsType.Undefined, engine.Evaluate("result").Type);
        engine.Evaluate("release(8);");
        engine.DrainJobs();
        Assert.Equal("8", engine.Evaluate("result").ToJsString());
    }

    [Fact]
    public void RefusesAnUnboundedThenableJobChain()
    {
        var engine = new Engine(new EngineOptions { MaximumLoopIterations = 5 });
        engine.Evaluate("let t = { then: function (resolve) { resolve(t); } }; Promise.resolve(t);");
        Assert.Throws<InvalidOperationException>(engine.DrainJobs);
    }

    private static string RunRecorded(string script)
    {
        var engine = new Engine();
        engine.Evaluate("let result = ''; function record(value) { result += String(value); } " + script);
        engine.DrainJobs();
        return engine.Evaluate("result").ToJsString();
    }
}
