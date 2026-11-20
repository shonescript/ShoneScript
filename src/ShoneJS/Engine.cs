using ShoneJS.Parsing;
using ShoneJS.Runtime;

namespace ShoneJS;

public sealed class EngineOptions
{
    public int MaximumLoopIterations { get; set; } = 1_000_000;
}

public sealed class ParsedScript
{
    internal ParsedScript(List<Stmt> statements, bool strict)
    {
        Statements = statements;
        IsStrict = strict;
    }

    internal List<Stmt> Statements { get; }
    public bool IsStrict { get; }
}

public sealed class Engine
{
    private readonly Interpreter _interpreter = new();
    public EngineOptions Options { get; }
    public Engine(EngineOptions? options = null) => Options = options ?? new EngineOptions();

    public ParsedScript ParseScript(string source) => ParseScript(source, forceStrict: false);

    public ParsedScript ParseScript(string source, bool forceStrict)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(new Lexer(source).Scan(), forceStrict: forceStrict);
        var statements = parser.Parse();
        return new ParsedScript(statements, parser.IsStrict);
    }

    public JsValue Execute(ParsedScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumLoopIterations);
        _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations;
        return _interpreter.Execute(script.Statements, script.IsStrict);
    }

    public JsValue Evaluate(string source) => Execute(ParseScript(source));

    public void DrainJobs()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumLoopIterations);
        _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations;
        _interpreter.DrainJobs();
    }
    public void SetGlobal(string name, JsValue value) => _interpreter.SetGlobal(name, value);
    public JsValue GetGlobal(string name) => _interpreter.GetGlobal(name);
}
