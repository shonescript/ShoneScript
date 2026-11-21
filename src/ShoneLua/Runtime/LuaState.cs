// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneLua.Parsing;

namespace ShoneLua.Runtime;

// The set of names in a local scope is fixed before a closure can capture it.
// A later declaration creates another layer, rather than rebinding a name for older closures.
internal sealed class LuaScope(LuaScope? parent = null)
{
    private readonly Dictionary<string, LuaValue> _values = new(StringComparer.Ordinal);
    public LuaScope? Parent { get; } = parent;
    public bool Contains(string name) => _values.ContainsKey(name);
    public LuaValue Get(string name) => _values.GetValueOrDefault(name);
    public void Set(string name, LuaValue value)
    {
        if (Parent is null && value.Kind == LuaValueKind.Nil) _values.Remove(name);
        else _values[name] = value;
    }
}

internal sealed class LuaTable
{
    private readonly Dictionary<LuaValue, LuaValue> _fields = new();
    public LuaValue Get(LuaValue key) => _fields.GetValueOrDefault(key);
    public void Set(LuaValue key, LuaValue value)
    {
        if (value.Kind == LuaValueKind.Nil) _fields.Remove(key);
        else _fields[key] = value;
    }
}

internal abstract class LuaFunction;
internal sealed class LuaClosure(FunctionExpression syntax, LuaScope scope, string? fileName) : LuaFunction
{
    public FunctionExpression Syntax { get; } = syntax;
    public LuaScope Scope { get; } = scope;
    public string? FileName { get; } = fileName;
}
internal sealed class LuaNativeFunction(Func<TextWriter, LuaValue[], LuaValue> execute) : LuaFunction
{
    public Func<TextWriter, LuaValue[], LuaValue> Execute { get; } = execute;
}
