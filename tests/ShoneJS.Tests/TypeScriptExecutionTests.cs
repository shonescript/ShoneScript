using ShoneJS.Runtime;
using ShoneJS.TypeScript;

namespace ShoneJS.Tests;

public class TypeScriptExecutionTests
{
    [Fact]
    public void MissingCheckerPreventsExecution()
    {
        var engine = new Engine(new EngineOptions
        {
            TypeScript = new TypeScriptOptions { ToolPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "tsgo") }
        });
        engine.SetGlobal("changed", JsValue.From(false));
        Assert.Throws<TypeScriptCheckException>(() => engine.EvaluateTypeScript("changed = true;"));
        Assert.False(engine.GetGlobal("changed").ToBoolean());
    }

    [NativeCheckerTheory]
    [InlineData("interface Point { x: number }; type Axis = number; let point: Point = { x: 42 }; point.x satisfies Axis", "42")]
    [InlineData("let count = 0; function next() { count++; return 40; } enum Value { First = next(), Second = First + 2 } Value.Second === 42 && Value[42] === 'Second' && count === 1", "true")]
    [InlineData("enum Mode { Ready = 'ready' } Mode.Ready", "ready")]
    [InlineData("function id<T>(value: T): T { return value; } const x = id<Array<Array<number>>>([[40]]); const f = (n: number): number => n + (1 << 1); f(x[0][0])", "42")]
    [InlineData("const o = { x: 42, f() { const read = () => this.x; return read(); } }; o.f()", "42")]
    [InlineData("const id = <T>(value: T): T => value; const n = <number>id(42); n", "42")]
    [InlineData("let o = { x: 42, f: function () { return this.x; } }; o.f!()", "42")]
    public void ExecutesCheckedOriginalSource(string source, string expected)
    {
        var engine = CreateIntegrationEngine();
        Assert.Equal(expected, engine.EvaluateTypeScript(source).ToString());
    }

    [NativeCheckerFact]
    public void TypeErrorPreventsAllSideEffects()
    {
        var engine = CreateIntegrationEngine();
        engine.SetGlobal("changed", JsValue.From(false));
        var error = Assert.Throws<TypeScriptCheckException>(() => engine.EvaluateTypeScript(
            "var changed = true;\nlet n: number = 'x';"));
        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Code == "TS2322" && diagnostic.Line == 2 && diagnostic.Column == 5);
        Assert.False(engine.GetGlobal("changed").ToBoolean());
    }

    private static Engine CreateIntegrationEngine()
    {
        var tool = Environment.GetEnvironmentVariable("SHONEJS_TSGO");
        return new Engine(new EngineOptions { TypeScript = new TypeScriptOptions { ToolPath = tool } });
    }

    private sealed class NativeCheckerFactAttribute : FactAttribute
    {
        public NativeCheckerFactAttribute() => Skip = CheckerSkipReason;
    }

    private sealed class NativeCheckerTheoryAttribute : TheoryAttribute
    {
        public NativeCheckerTheoryAttribute() => Skip = CheckerSkipReason;
    }

    private static string? CheckerSkipReason => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SHONEJS_TSGO"))
        ? "Set SHONEJS_TSGO to run the official checker integration tests."
        : null;

    [Fact]
    public void JavaScriptEntryPointDoesNotAcceptTypeAnnotations() =>
        Assert.Throws<Parsing.JsSyntaxException>(() => new Engine().Evaluate("let answer: number = 42;"));
}
