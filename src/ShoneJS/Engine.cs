// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneJs;

/// <summary>Configures execution limits for an engine.</summary>
public sealed class EngineOptions
{
    /// <summary>Sets the iteration budget used by script execution and Promise job draining.</summary>
    public int MaximumLoopIterations { get; set; } = 1_000_000;
}

/// <summary>A parsed JavaScript script that can be passed to <see cref="Engine.Execute"/>.</summary>
public sealed class ParsedScript
{
    internal ParsedScript(List<Stmt> statements, bool strict)
    {
        Statements = statements;
        IsStrict = strict;
    }

    internal List<Stmt> Statements { get; }
    /// <summary>Indicates whether the script was parsed in strict mode.</summary>
    public bool IsStrict { get; }
}

/// <summary>A parsed TypeScript script ready for direct execution.</summary>
public sealed class CheckedTypeScript
{
    internal CheckedTypeScript(ParsedScript script) => Script = script;
    internal ParsedScript Script { get; }
}

/// <summary>Parses and interprets JavaScript and supported TypeScript scripts.</summary>
/// <remarks>Executions on an instance share globals and pending Promise jobs. Use a separate instance for independent sessions.</remarks>
public sealed class Engine
{
    private readonly Interpreter _interpreter = new();
    /// <summary>Gets the configuration used by subsequent calls.</summary>
    public EngineOptions Options { get; }
    /// <summary>Creates an engine with a fresh global environment.</summary>
    public Engine(EngineOptions? options = null) => Options = options ?? new EngineOptions();

    /// <summary>Parses JavaScript without executing it.</summary>
    public ParsedScript ParseScript(string source) => ParseScript(source, forceStrict: false);

    /// <summary>Parses JavaScript with an option to enable strict mode without altering source positions.</summary>
    public ParsedScript ParseScript(string source, bool forceStrict)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(new Lexer(source).Scan(), forceStrict: forceStrict);
        var statements = parser.Parse();
        return new ParsedScript(statements, parser.IsStrict);
    }

    /// <summary>Executes a parsed script and returns its completion value.</summary>
    public JsValue Execute(ParsedScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumLoopIterations);
        _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations;
        return _interpreter.Execute(script.Statements, script.IsStrict);
    }

    /// <summary>Parses and executes JavaScript in the current global environment.</summary>
    public JsValue Evaluate(string source) => Execute(ParseScript(source));

    /// <summary>Parses supported TypeScript syntax without executing it.</summary>
    /// <exception cref="JsSyntaxException">The source could not be parsed by ShoneJs.</exception>
    public CheckedTypeScript ParseTypeScript(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new Parser(new Lexer(source).Scan(), typeScript: true);
        var statements = parser.ParseTypeScript();
        return new CheckedTypeScript(new ParsedScript(statements, parser.IsStrict));
    }

    /// <summary>Interprets a previously parsed TypeScript script directly.</summary>
    public JsValue ExecuteTypeScript(CheckedTypeScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Execute(script.Script);
    }

    /// <summary>Parses and executes supported TypeScript syntax directly in the current global environment.</summary>
    public JsValue EvaluateTypeScript(string source) => ExecuteTypeScript(ParseTypeScript(source));

    /// <summary>Runs the pending Promise jobs. Hosts call this at a job checkpoint.</summary>
    public void DrainJobs()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumLoopIterations);
        _interpreter.MaximumLoopIterations = Options.MaximumLoopIterations;
        _interpreter.DrainJobs();
    }
    /// <summary>Sets a binding in this engine's global environment.</summary>
    public void SetGlobal(string name, JsValue value) => _interpreter.SetGlobal(name, value);
    /// <summary>Reads a binding from this engine's global environment.</summary>
    public JsValue GetGlobal(string name) => _interpreter.GetGlobal(name);
}
