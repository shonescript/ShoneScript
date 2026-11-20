// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections;

namespace ShonePy;

public sealed class PyList : IList<PyValue>
{
    private readonly List<PyValue> _items;
    public PyList() => _items = [];
    public PyList(IEnumerable<PyValue> items) => _items = [.. items];
    public int Count => _items.Count;
    public bool IsReadOnly => false;
    public PyValue this[int index] { get => _items[index]; set => _items[index] = value; }
    public void Add(PyValue item) => _items.Add(item);
    public void Clear() => _items.Clear();
    public bool Contains(PyValue item) => _items.Contains(item);
    public void CopyTo(PyValue[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    public IEnumerator<PyValue> GetEnumerator() => _items.GetEnumerator();
    public int IndexOf(PyValue item) => _items.IndexOf(item);
    public void Insert(int index, PyValue item) => _items.Insert(index, item);
    public bool Remove(PyValue item) => _items.Remove(item);
    public void RemoveAt(int index) => _items.RemoveAt(index);
    internal void ReplaceRange(int start, int count, List<PyValue> replacement)
    {
        _items.RemoveRange(start, count);
        _items.InsertRange(start, replacement);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => "[" + string.Join(", ", _items.Select(x => x.Repr())) + "]";
}

public sealed class PyTuple : IReadOnlyList<PyValue>
{
    private readonly PyValue[] _items;
    public PyTuple(IEnumerable<PyValue> items) => _items = items.ToArray();
    public PyTuple(params PyValue[] items) => _items = [.. items];
    public int Count => _items.Length;
    public PyValue this[int index] => _items[index];
    public IEnumerator<PyValue> GetEnumerator() => ((IEnumerable<PyValue>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => _items.Length == 1 ? $"({_items[0].Repr()},)" : "(" + string.Join(", ", _items.Select(x => x.Repr())) + ")";
}

public class PyDict : IEnumerable<KeyValuePair<PyValue, PyValue>>
{
    private readonly List<KeyValuePair<PyValue, PyValue>> _items = [];
    public int Count => _items.Count;
    public PyValue this[PyValue key]
    {
        get => TryGetValue(key, out var value) ? value : MissingValue(key);
        set { var i = Find(key); if (i < 0) _items.Add(new(key, value)); else _items[i] = new(_items[i].Key, value); }
    }
    internal virtual PyValue MissingValue(PyValue key) => throw Runtime.PyExceptions.KeyError(key);
    internal bool TryGetValue(PyValue key, out PyValue value)
    {
        var index = Find(key);
        value = index < 0 ? PyValue.None : _items[index].Value;
        return index >= 0;
    }
    public bool ContainsKey(PyValue key) => Find(key) >= 0;
    public bool Remove(PyValue key) { var i = Find(key); if (i < 0) return false; _items.RemoveAt(i); return true; }
    public IEnumerator<KeyValuePair<PyValue, PyValue>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    private int Find(PyValue key) { PyOperations.EnsureHashable(key); return _items.FindIndex(pair => PyOperations.Equal(pair.Key, key)); }
    public override string ToString() => "{" + string.Join(", ", _items.Select(x => x.Key.Repr() + ": " + x.Value.Repr())) + "}";
}

public sealed class PyCallable(Func<IReadOnlyList<PyValue>, PyValue> invoke, string? name = null)
{
    public string? Name { get; } = name;
    internal PyDict? Attributes { get; init; }
    internal bool IsPythonFunction { get; init; }
    internal PyValue Doc { get; init; }
    internal Func<IReadOnlyList<PyValue>, IReadOnlyDictionary<string, PyValue>, PyValue>? InvokeWithKeywords { get; init; }
    public PyValue Invoke(IReadOnlyList<PyValue> arguments) => invoke(arguments);

    internal PyValue Invoke(IReadOnlyList<PyValue> arguments, IReadOnlyDictionary<string, PyValue> keywords) =>
        InvokeWithKeywords is { } bind ? bind(arguments, keywords)
            : throw new PythonException("TypeError", $"{Name ?? "function"}() takes no keyword arguments");
}
