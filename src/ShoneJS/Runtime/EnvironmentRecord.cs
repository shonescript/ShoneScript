// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneJs.Runtime;

/// <summary>A lexical scope whose bindings may remain uninitialized until declaration execution.</summary>
public sealed class EnvironmentRecord(EnvironmentRecord? parent = null)
{
    internal sealed class Binding(bool mutable, bool lexical)
    {
        internal bool Lexical { get; } = lexical;
        private bool _initialized;
        private JsValue _value;
        private Func<JsValue>? _readAlias;

        internal void Initialize(JsValue value)
        {
            _value = value;
            _initialized = true;
        }

        internal void InitializeImport(Func<JsValue> read)
        {
            _readAlias = read;
            _initialized = true;
        }

        internal JsValue Read(string name)
        {
            CheckInitialized(name);
            return _readAlias is { } read ? read() : _value;
        }

        internal void Write(string name, JsValue value)
        {
            CheckInitialized(name);
            if (!mutable) throw JsException.TypeError($"Assignment to constant '{name}'");
            _value = value;
        }

        // Uninitialized lexical bindings implement the temporal dead zone.
        private void CheckInitialized(string name)
        {
            if (!_initialized) throw JsException.ReferenceError($"Cannot access '{name}' before initialization");
        }
    }

    private Dictionary<string, Binding>? _bindings;
    public EnvironmentRecord? Parent { get; } = parent;
    internal bool HasOwn(string name) => _bindings?.ContainsKey(name) == true;
    internal bool HasLexical(string name) => _bindings?.TryGetValue(name, out var binding) == true && binding.Lexical;

    /// <summary>Creates and initializes a mutable non-lexical binding.</summary>
    public void Declare(string name, JsValue value) => Create(name, mutable: true, lexical: false).Initialize(value);

    internal Binding Create(string name, bool mutable, bool lexical)
    {
        var binding = new Binding(mutable, lexical);
        if (!(_bindings ??= new(StringComparer.Ordinal)).TryAdd(name, binding))
            throw new JsException(JsValue.From(new JsError("SyntaxError", $"Identifier '{name}' has already been declared")));
        return binding;
    }

    internal void CreateImport(string name, Func<JsValue> read) => Create(name, mutable: false, lexical: true).InitializeImport(read);

    internal void Initialize(string name, JsValue value) =>
        (_bindings?[name] ?? throw new KeyNotFoundException()).Initialize(value);

    internal Binding? ResolveBinding(string name, out EnvironmentRecord? environment)
    {
        for (environment = this; environment is not null; environment = environment.Parent)
            if (environment._bindings?.TryGetValue(name, out var binding) == true) return binding;
        return null;
    }

    public JsValue Get(string name) => RequireBinding(name).Read(name);

    public void Set(string name, JsValue value) => RequireBinding(name).Write(name, value);

    private Binding RequireBinding(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return ResolveBinding(name, out _) ?? throw JsException.ReferenceError($"'{name}' is not defined");
    }
}
