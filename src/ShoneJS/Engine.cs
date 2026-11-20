using ShoneJS.Parsing;
using ShoneJS.Runtime;
using ShoneJS.TypeScript;

namespace ShoneJS;

public sealed class EngineOptions
{
    public int MaximumLoopIterations { get; set; } = 1_000_000;
    public TypeScriptOptions TypeScript { get; set; } = new();
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

public sealed class CheckedTypeScript
{
    internal CheckedTypeScript(ParsedScript script) => Script = script;
    internal ParsedScript Script { get; }
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

    public CheckedTypeScript ParseTypeScript(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(new Lexer(source).Scan(), typeScript: true);
        var statements = parser.ParseTypeScript();
        var check = new TypeScriptChecker().Check(source, Options.TypeScript);
        if (!check.Succeeded) throw new TypeScriptCheckException(check.Diagnostics);
        return new CheckedTypeScript(new ParsedScript(statements, parser.IsStrict));
    }

    public JsValue ExecuteTypeScript(CheckedTypeScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Execute(script.Script);
    }

    public JsValue EvaluateTypeScript(string source) => ExecuteTypeScript(ParseTypeScript(source));

    public void DrainJobs()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumLoopIterations);
        _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations;
        _interpreter.DrainJobs();
    }
    public void SetGlobal(string name, JsValue value) => _interpreter.SetGlobal(name, value);
    public JsValue GetGlobal(string name) => _interpreter.GetGlobal(name);
}
