namespace ShoneJS.Runtime;

/// <summary>A lexical scope whose bindings may remain uninitialized until declaration execution.</summary>
public sealed class EnvironmentRecord(EnvironmentRecord? parent = null)
{
    private sealed class Binding(bool mutable, bool lexical)
    {
        public readonly bool Mutable = mutable;
        public readonly bool Lexical = lexical;
        public bool Initialized;
        public JsValue Value;
    }

    private readonly Dictionary<string, Binding> _bindings = new(StringComparer.Ordinal);
    public EnvironmentRecord? Parent { get; } = parent;
    internal bool HasOwn(string name) => _bindings.ContainsKey(name);
    internal bool HasLexical(string name) => _bindings.TryGetValue(name, out var binding) && binding.Lexical;

    /// <summary>Creates and initializes a mutable non-lexical binding.</summary>
    public void Declare(string name, JsValue value)
    {
        Create(name, mutable: true, lexical: false);
        Initialize(name, value);
    }

    internal void Create(string name, bool mutable, bool lexical)
    {
        if (!_bindings.TryAdd(name, new Binding(mutable, lexical)))
            throw new JsException(JsValue.From(new JsError("SyntaxError", $"Identifier '{name}' has already been declared")));
    }

    internal void Initialize(string name, JsValue value)
    {
        var binding = _bindings[name];
        binding.Value = value;
        binding.Initialized = true;
    }

    internal EnvironmentRecord? Resolve(string name)
    {
        for (EnvironmentRecord? scope = this; scope is not null; scope = scope.Parent)
            if (scope.HasOwn(name)) return scope;
        return null;
    }

    public JsValue Get(string name)
    {
        var scope = Resolve(name) ?? throw JsException.ReferenceError($"'{name}' is not defined");
        return scope.GetBinding(name).Value;
    }

    public void Set(string name, JsValue value)
    {
        var scope = Resolve(name) ?? throw JsException.ReferenceError($"'{name}' is not defined");
        var binding = scope.GetBinding(name);
        if (!binding.Mutable) throw JsException.TypeError($"Assignment to constant '{name}'");
        binding.Value = value;
    }

    // Uninitialized lexical bindings implement the temporal dead zone.
    private Binding GetBinding(string name)
    {
        var binding = _bindings[name];
        if (!binding.Initialized) throw JsException.ReferenceError($"Cannot access '{name}' before initialization");
        return binding;
    }
}
