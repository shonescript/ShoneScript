// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class TypeScriptExecutionTests
{
    [Theory]
    [InlineData("interface Point { x: number }; type Axis = number; let point: Point = { x: 42 }; point.x satisfies Axis", "42")]
    [InlineData("let count = 0; function next() { count++; return 40; } enum Value { First = next(), Second = First + 2 } Value.Second === 42 && Value[42] === 'Second' && count === 1", "true")]
    [InlineData("enum Mode { Ready = 'ready' } Mode.Ready", "ready")]
    [InlineData("function id<T>(value: T): T { return value; } const x = id<Array<Array<number>>>([[40]]); const f = (n: number): number => n + (1 << 1); f(x[0][0])", "42")]
    [InlineData("const o = { x: 42, f() { const read = () => this.x; return read(); } }; o.f()", "42")]
    [InlineData("const id = <T>(value: T): T => value; const n = <number>id(42); n", "42")]
    [InlineData("let o = { x: 42, f: function () { return this.x; } }; o.f!()", "42")]
    public void ExecutesTypeScriptDirectly(string source, string expected)
    {
        var engine = new JsEngine();
        Assert.Equal(expected, engine.EvaluateTypeScript(source).ToString());
    }

    [Fact]
    public void ParseTypeScriptDoesNotExecuteSource()
    {
        var engine = new JsEngine();
        engine.SetGlobal("changed", JsValue.From(false));

        var parsed = engine.ParseTypeScript("changed = true;");

        Assert.False(engine.GetGlobal("changed").ToBoolean());
        engine.ExecuteTypeScript(parsed);
        Assert.True(engine.GetGlobal("changed").ToBoolean());
    }

    [Fact]
    public void TypeScriptSyntaxErrorsAreReportedByParser() =>
        Assert.Throws<Parsing.JsSyntaxException>(() => new JsEngine().EvaluateTypeScript("let answer: number = ;"));

    [Fact]
    public void TypeScriptTypesAreNotStaticallyChecked() =>
        Assert.Equal("x", new JsEngine().EvaluateTypeScript("let value: number = 'x'; value").ToString());

    [Fact]
    public void JavaScriptEntryPointDoesNotAcceptTypeAnnotations() =>
        Assert.Throws<Parsing.JsSyntaxException>(() => new JsEngine().Evaluate("let answer: number = 42;"));
}
