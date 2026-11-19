namespace ShoneJS.Runtime;

public class JsObject
{
    private readonly Dictionary<string, JsValue> _properties = new(StringComparer.Ordinal);
    public JsObject? Prototype { get; set; }
    public virtual JsValue Get(string key) => _properties.TryGetValue(key, out var v) ? v : Prototype?.Get(key) ?? JsValue.Undefined;
    public virtual void Set(string key, JsValue value) => _properties[key] = value;
    public bool HasOwn(string key) => _properties.ContainsKey(key);
    public IEnumerable<KeyValuePair<string, JsValue>> Properties => _properties;
}
public sealed class JsArray : JsObject
{
    private readonly List<JsValue> _items = [];
    public int Length => _items.Count;
    public void Add(JsValue value) => _items.Add(value);
    public override JsValue Get(string key) => key == "length" ? JsValue.From((double)Length) : int.TryParse(key, out var i) && i >= 0 && i < Length ? _items[i] : base.Get(key);
    public override void Set(string key, JsValue value) { if (key == "length" && value.ToNumber() >= 0) { var n = (int)value.ToNumber(); while (_items.Count > n) _items.RemoveAt(_items.Count - 1); while (_items.Count < n) _items.Add(JsValue.Undefined); } else if (int.TryParse(key, out var i) && i >= 0 && i < 100000) { while (_items.Count <= i) _items.Add(JsValue.Undefined); _items[i] = value; } else base.Set(key, value); }
}
public sealed class JsFunction(Func<JsValue[], JsValue> call, int arity = 0)
{
    public int Arity { get; } = arity;
    public JsValue Call(params JsValue[] args) => call(args);
}
