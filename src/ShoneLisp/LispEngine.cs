// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneLisp.Parsing;
using ShoneLisp.Runtime;

namespace ShoneLisp;

public sealed class LispOptions
{
    internal const int DepthCeiling = 256;

    public TextWriter Output { get; set; } = TextWriter.Null;

    /// <summary>Positive execution budget renewed for each script execution.</summary>
    public int MaximumSteps { get; set; } = 1_000_000;

    /// <summary>Maximum reader nesting, between 1 and 256.</summary>
    public int MaximumParseDepth { get; set; } = 128;

    /// <summary>Maximum nesting during evaluation, including calls, between 1 and 256.</summary>
    public int MaximumEvaluationDepth { get; set; } = 128;

    internal static void ValidateDepth(int value, string name)
    {
        if (value is < 1 or > DepthCeiling)
            throw new ArgumentOutOfRangeException(name, value, $"Depth must be between 1 and {DepthCeiling}.");
    }
}

public sealed class ParsedLispScript
{
    internal ParsedLispScript(LispExpression[] expressions, string? fileName)
    {
        Expressions = expressions;
        FileName = fileName;
    }

    internal LispExpression[] Expressions { get; }
    internal string? FileName { get; }
}

/// <summary>Interprets Scheme-style source in a persistent global environment.</summary>
public sealed class LispEngine
{
    private readonly LispInterpreter _interpreter = new();
    public LispOptions Options { get; }

    public LispEngine(LispOptions? options = null, TextWriter? output = null)
    {
        Options = options ?? new LispOptions();
        if (output is not null)
            Options.Output = output;
    }

    public ParsedLispScript ParseScript(string source, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        int depth = Options.MaximumParseDepth;
        LispOptions.ValidateDepth(depth, nameof(Options.MaximumParseDepth));
        return new LispReader(source, fileName, depth).ReadScript();
    }

    public LispValue Execute(ParsedLispScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return _interpreter.Execute(script, Options);
    }

    public LispValue Execute(string source, string? fileName = null) => Execute(ParseScript(source, fileName));
    public LispValue Evaluate(string source, string? fileName = null) => Execute(source, fileName);

    public void SetGlobal(string name, LispValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);
        _interpreter.SetGlobal(name, value);
    }

    public LispValue GetGlobal(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _interpreter.GetGlobal(name);
    }
}
