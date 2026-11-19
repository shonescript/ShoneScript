namespace ShoneJS.Runtime;

public sealed class EnvironmentRecord(EnvironmentRecord? parent = null)
{
    private readonly Dictionary<string, JsValue> _values = new(StringComparer.Ordinal);
    public EnvironmentRecord? Parent { get; } = parent;
    public void Declare(string name, JsValue value) { if (!_values.TryAdd(name, value)) throw new InvalidOperationException($"Identifier '{name}' has already been declared"); }
    public JsValue Get(string name) => _values.TryGetValue(name, out var v) ? v : Parent?.Get(name) ?? throw new InvalidOperationException($"'{name}' is not defined");
    public void Set(string name, JsValue value) { if (_values.ContainsKey(name)) _values[name] = value; else if (Parent is not null) Parent.Set(name, value); else throw new InvalidOperationException($"'{name}' is not defined"); }
}
