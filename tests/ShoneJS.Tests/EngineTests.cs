using ShoneJS;
using ShoneJS.Parsing;
using ShoneJS.Runtime;

namespace ShoneJS.Tests;

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
    public void EvaluatesCoreScripts(string script, double expected) => Assert.Equal(expected, (double)new Engine().Evaluate(script).Data!);

    [Fact] public void CoercesStringsAndConditions() => Assert.Equal("v=6", new Engine().Evaluate("let x = 2; `v=${x * 3}`").ToJsString());
    [Fact] public void LexerTracksSyntaxLocation() { var ex = Assert.Throws<JsSyntaxException>(() => new Lexer("let x = @").Scan()); Assert.Equal(1, ex.Line); Assert.Equal(9, ex.Column); }
    [Fact] public void ThrowsForUnknownIdentifier() => Assert.Throws<InvalidOperationException>(() => new Engine().Evaluate("missing"));
    [Fact] public void ExposesPersistentGlobalBindings() { var engine = new Engine(); engine.SetGlobal("seed", JsValue.From(4d)); Assert.Equal(7d, (double)engine.Evaluate("seed + 3").Data!); }
    [Fact] public void EnforcesLoopExecutionBudget() => Assert.Throws<InvalidOperationException>(() => new Engine(new EngineOptions { MaximumLoopIterations = 3 }).Evaluate("while (true) {}"));
}
