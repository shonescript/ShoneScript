// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Runtime;

/// <summary>A lexical environment with parent-linked closure lookup.</summary>
public sealed class PyScope
{
    private readonly Dictionary<string, PyValue> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _locals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _globals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _nonlocals = new(StringComparer.Ordinal);
    private readonly PyScope? _builtins;
    public PyScope? Parent { get; }
    internal bool IsClassScope { get; init; }
    internal PyClassCell? DefiningClass { get; init; }
    internal string? FirstPositionalParameter { get; init; }
    internal PyScope Closure => IsClassScope ? Parent! : this;
    internal PyValue ModuleName => Root()._values.GetValueOrDefault("__name__", PyValue.From("__main__"));

    public PyScope(PyScope? parent = null) => Parent = parent;
    internal PyScope(PyScope? parent, PyScope builtins) : this(parent) => _builtins = builtins;
    public bool ContainsLocal(string name) => _values.ContainsKey(name);
    internal bool TryGetLocal(string name, out PyValue value) => _values.TryGetValue(name, out value);
    public bool TryGet(string name, out PyValue value)
    {
        for (PyScope? scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._values.TryGetValue(name, out value)) return true;
            if (scope._locals.Contains(name))
            {
                value = PyValue.None;
                throw new PythonException("UnboundLocalError", $"cannot access local variable '{name}' where it is not associated with a value");
            }
        }
        if (Root()._builtins is { } builtins && builtins._values.TryGetValue(name, out value)) return true;
        value = PyValue.None;
        return false;
    }
    public PyValue Get(string name)
    {
        if (Parent is not null && _globals.Contains(name)) return Root().Get(name);
        if (_nonlocals.Contains(name))
        {
            for (PyScope? scope = Parent; scope?.Parent is not null; scope = scope.Parent)
            {
                if (scope._values.TryGetValue(name, out PyValue value)) return value;
                if (scope._locals.Contains(name)) throw new PythonException("UnboundLocalError", $"cannot access local variable '{name}' where it is not associated with a value");
            }
            throw new PythonException("SyntaxError", $"no binding for nonlocal '{name}' found");
        }
        for (PyScope? scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.Parent is not null && scope._globals.Contains(name)) return scope.Root().Get(name);
            if (scope._values.TryGetValue(name, out PyValue value)) return value;
            if (scope._locals.Contains(name))
            {
                if (scope.IsClassScope) return scope.Root().Get(name);
                if (scope != this)
                    throw new PythonException("NameError", $"cannot access free variable '{name}' where it is not associated with a value in enclosing scope");
                throw new PythonException("UnboundLocalError", $"cannot access local variable '{name}' where it is not associated with a value");
            }
        }
        if (Root()._builtins is { } builtins && builtins._values.TryGetValue(name, out var builtin)) return builtin;
        throw new PythonException("NameError", $"name '{name}' is not defined");
    }
    public void DeclareLocal(string name) => _locals.Add(name);
    public void DeclareGlobal(string name) { _globals.Add(name); _locals.Remove(name); }
    public void DeclareNonlocal(string name) { _nonlocals.Add(name); _locals.Remove(name); }
    public bool HasEnclosingBinding(string name)
    {
        for (PyScope? scope = Parent; scope?.Parent is not null; scope = scope.Parent)
            if (scope._values.ContainsKey(name) || scope._locals.Contains(name)) return true;
        return false;
    }
    public void Define(string name, PyValue value) => _values[name] = value;
    public void Assign(string name, PyValue value) => AssignmentScope(name)._values[name] = value;
    internal void ClearBinding(string name) => AssignmentScope(name)._values.Remove(name);

    private PyScope AssignmentScope(string name)
    {
        if (_globals.Contains(name)) return Root();
        if (_nonlocals.Contains(name))
        {
            for (PyScope? scope = Parent; scope?.Parent is not null; scope = scope.Parent)
                if (scope._locals.Contains(name) || scope._values.ContainsKey(name)) return scope;
            throw new PythonException("SyntaxError", $"no binding for nonlocal '{name}' found");
        }
        return this;
    }
    private PyScope Root() { PyScope scope = this; while (scope.Parent is not null) scope = scope.Parent; return scope; }
    public IEnumerable<KeyValuePair<string, PyValue>> LocalValues => _values;
}

// Function definitions capture the cell before the class body has finished running.
internal sealed class PyClassCell
{
    internal PyClass? Value { get; set; }
}
