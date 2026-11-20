// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Parsing;
using ShonePy.Runtime;

namespace ShonePy;

public sealed class PythonOptions
{
    public TextWriter Output { get; set; } = TextWriter.Null;
    public int MaximumSteps { get; set; } = 1_000_000;
    public int MaximumCallDepth { get; set; } = 128;
    public IList<string> SearchPaths { get; } = new List<string>();
}

public sealed class ParsedPythonScript
{
    internal ParsedPythonScript(ModuleSyntax syntax) => Syntax = syntax;
    internal ModuleSyntax Syntax { get; }
}

/// <summary>Interprets Python source in a persistent global environment.</summary>
public sealed class PythonEngine
{
    private readonly PyInterpreter _interpreter;
    public PythonOptions Options { get; }

    public PythonEngine(PythonOptions? options = null)
    {
        Options = options ?? new PythonOptions();
        _interpreter = new PyInterpreter(Options);
    }

    public ParsedPythonScript ParseScript(string source, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ParsedPythonScript(Parser.Parse(source, fileName));
    }

    public PyValue Execute(ParsedPythonScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return _interpreter.Execute(script.Syntax);
    }

    public PyValue Evaluate(string source, string? fileName = null) => Execute(ParseScript(source, fileName));

    /// <summary>Runs a managed coroutine created by this engine to completion and returns its result.</summary>
    public PyValue RunCoroutine(PyValue coroutine) => _interpreter.RunCoroutine(coroutine);

    public void SetGlobal(string name, PyValue value) => _interpreter.SetGlobal(name, value);
    public PyValue GetGlobal(string name) => _interpreter.GetGlobal(name);
}
