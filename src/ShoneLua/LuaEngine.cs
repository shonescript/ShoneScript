// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneLua.Parsing;
using ShoneLua.Runtime;

namespace ShoneLua;

public sealed class LuaOptions
{
    private const int DepthCeiling = 256;
    public TextWriter Output { get; set; } = TextWriter.Null;

    /// <summary>Positive execution budget renewed for each chunk.</summary>
    public int MaximumSteps { get; set; } = 1_000_000;

    /// <summary>Maximum parser nesting, between 1 and 256.</summary>
    public int MaximumParseDepth { get; set; } = 128;

    /// <summary>Maximum expression and block nesting (including within calls), between 1 and 256.</summary>
    public int MaximumEvaluationDepth { get; set; } = 256;

    /// <summary>Independent maximum depth of function calls, between 1 and 256.</summary>
    public int MaximumCallDepth { get; set; } = 128;

    internal static int ValidateDepth(int value, string name) => value is < 1 or > DepthCeiling
        ? throw new ArgumentOutOfRangeException(name, value, $"Depth must be between 1 and {DepthCeiling}.")
        : value;
}

public sealed class ParsedLuaScript
{
    internal ParsedLuaScript(Block syntax, string? fileName) => (Syntax, FileName) = (syntax, fileName);
    internal Block Syntax { get; }
    public string? FileName { get; }
}

/// <summary>The value of a chunk, with a flag distinguishing an explicit value (even nil) from no value.</summary>
public readonly record struct LuaExecutionResult(bool HasReturnValue, LuaValue Value);

/// <summary>Runs Lua chunks with fresh lexical locals and persistent, instance-specific globals.</summary>
public sealed class LuaEngine
{
    private readonly LuaInterpreter _interpreter;
    public LuaOptions Options { get; }

    public LuaEngine(LuaOptions? options = null, TextWriter? output = null)
    {
        Options = options ?? new LuaOptions();
        if (output is not null) Options.Output = output;
        _interpreter = new LuaInterpreter(Options);
    }

    public ParsedLuaScript ParseScript(string source, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var depth = LuaOptions.ValidateDepth(Options.MaximumParseDepth, nameof(Options.MaximumParseDepth));
        return new ParsedLuaScript(new LuaParser(source, fileName, depth).Parse(), fileName);
    }

    public LuaExecutionResult Execute(ParsedLuaScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return _interpreter.Execute(script);
    }

    public LuaExecutionResult Execute(string source, string? fileName = null) => Execute(ParseScript(source, fileName));
    public LuaExecutionResult Evaluate(string source, string? fileName = null) => Execute(source, fileName);

    public LuaValue GetGlobal(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _interpreter.GetGlobal(name);
    }

    public void SetGlobal(string name, LuaValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _interpreter.SetGlobal(name, value);
    }
}
