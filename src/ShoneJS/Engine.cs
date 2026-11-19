using ShoneJS.Parsing;
using ShoneJS.Runtime;

namespace ShoneJS;

public sealed class EngineOptions { public int MaximumLoopIterations { get; set; } = 1_000_000; }
public sealed class Engine
{
    private readonly Interpreter _interpreter = new();
    public EngineOptions Options { get; }
    public Engine(EngineOptions? options = null) { Options = options ?? new EngineOptions(); _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations; }
    public JsValue Evaluate(string source) { var tokens = new Lexer(source).Scan(); var statements = new Parser(tokens).Parse(); return _interpreter.Execute(statements); }
    public void SetGlobal(string name, JsValue value) => _interpreter.SetGlobal(name, value);
    public JsValue GetGlobal(string name) => _interpreter.GetGlobal(name);
}
