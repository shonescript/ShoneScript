// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs;
using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class EngineTests
{
    [Theory]
    [InlineData("1 + 2 * 3", 7d)]
    [InlineData("(10 - 4) / 2", 3d)]
    [InlineData("let x = 4; x = x + 3; x", 7d)]
    [InlineData("function twice(x) { return x * 2; } twice(6)", 12d)]
    [InlineData("let make = function(x) { return function(y) { return x + y; }; }; let add = make(5); add(3)", 8d)]
    [InlineData("let a = [2, 4]; a[1] + a.length", 6d)]
    [InlineData("let o = {x: 3}; o.x = 9; o.x", 9d)]
    [InlineData("let i = 0; while (i < 4) { i = i + 1; } i", 4d)]
    public void EvaluatesCoreScripts(string script, double expected) => Assert.Equal(expected, (double)new JsEngine().Evaluate(script).Data!);

    [Fact] public void CoercesStringsAndConditions() => Assert.Equal("v=6", new JsEngine().Evaluate("let x = 2; `v=${x * 3}`").ToJsString());
    [Fact] public void LexerTracksSyntaxLocation() { var ex = Assert.Throws<JsSyntaxException>(() => new Lexer("let x = @").Scan()); Assert.Equal(1, ex.Line); Assert.Equal(9, ex.Column); }
    [Fact] public void ThrowsForUnknownIdentifier() => Assert.Equal("ReferenceError", Assert.Throws<JsException>(() => new JsEngine().Evaluate("missing")).ErrorName);
    [Fact] public void ExposesPersistentGlobalBindings() { var engine = new JsEngine(); engine.SetGlobal("seed", JsValue.From(4d)); Assert.Equal(7d, (double)engine.Evaluate("seed + 3").Data!); }
    [Fact] public void EnforcesLoopExecutionBudget() => Assert.Throws<InvalidOperationException>(() => new JsEngine(new JsEngineOptions { MaximumLoopIterations = 3 }).Evaluate("while (true) {}"));

    [Fact]
    public void AppliesSharedOptionChangesToEachEngine()
    {
        var options = new JsEngineOptions();
        var engines = new[] { new JsEngine(options), new JsEngine(options) };
        var script = engines[0].ParseScript("1 + 2");
        foreach (var engine in engines) Assert.Equal(3d, engine.Execute(script).ToNumber());

        options.MaximumSteps = 1;
        Assert.Equal(1, options.MaximumExecutionSteps);
        foreach (var engine in engines) Assert.Throws<InvalidOperationException>(() => engine.Execute(script));

        options.MaximumExecutionSteps = 20;
        Assert.Equal(20, options.MaximumSteps);
        foreach (var engine in engines) Assert.Equal(3d, engine.Execute(script).ToNumber());

        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        options.CancellationToken = lifetime.Token;
        foreach (var engine in engines) Assert.Throws<OperationCanceledException>(() => engine.Execute(script));
    }

    [Fact]
    public void SymbolRegistrySharesByDefaultAndCanBeIsolatedAtConstruction()
    {
        const string source = "Symbol.for('engine-registry-test')";
        var shared = new JsEngine().Evaluate(source);
        Assert.Same(shared.Data, new JsEngine().Evaluate(source).Data);

        var options = new JsEngineOptions { IsolateSymbolRegistry = true };
        var first = new JsEngine(options);
        var second = new JsEngine(options);
        var isolated = first.Evaluate(source);
        Assert.NotSame(shared.Data, isolated.Data);
        Assert.NotSame(isolated.Data, second.Evaluate(source).Data);
        Assert.Same(isolated.Data, first.Evaluate(source).Data);
        Assert.Equal("engine-registry-test", first.Evaluate($"Symbol.keyFor({source})").ToJsString());
    }
}
