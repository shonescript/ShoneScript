// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections;
using ShonePy.Runtime;

namespace ShonePy;

public sealed class PySet : IReadOnlyCollection<PyValue>
{
    private sealed class ElementComparer : IEqualityComparer<PyValue>
    {
        public bool Equals(PyValue left, PyValue right)
        {
            // A hash collision can introduce extra comparisons beyond the outer loop's item charge.
            Charge();
            return (left.Type == right.Type && ReferenceEquals(left.Data, right.Data)) || PyOperations.Equal(left, right);
        }

        public int GetHashCode(PyValue value) => PyOperations.Hash(value);
    }

    private static readonly ElementComparer Comparer = new();
    private HashSet<PyValue> _items = new(Comparer);
    private int _version;

    public PySet() { }
    public PySet(IEnumerable<PyValue> source) => Update(source);

    public int Count => _items.Count;

    public bool Add(PyValue item)
    {
        if (!_items.Add(item)) return false;
        _version++;
        return true;
    }

    // Python also permits a set as a lookup key by treating it as a frozenset.
    // There are no frozenset members in this runtime, so such a lookup always misses.
    public bool Contains(PyValue item) => item.Type != PyType.Set && ContainsHashable(item);

    private bool ContainsHashable(PyValue item)
    {
        PyOperations.EnsureHashable(item);
        return _items.Contains(item);
    }

    public bool Remove(PyValue item) => item.Type != PyType.Set && RemoveHashable(item);

    private bool RemoveHashable(PyValue item)
    {
        PyOperations.EnsureHashable(item);
        if (!_items.Remove(item)) return false;
        _version++;
        return true;
    }

    public void Clear()
    {
        if (Count == 0) return;
        _items.Clear();
        _version++;
    }

    public IEnumerator<PyValue> GetEnumerator() => Enumerate(_version).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private IEnumerable<PyValue> Enumerate(int version)
    {
        using var cursor = _items.GetEnumerator();
        while (true)
        {
            if (_version != version) throw new PythonException("RuntimeError", "Set changed size during iteration");
            if (!cursor.MoveNext()) yield break;
            Charge();
            yield return cursor.Current;
        }
    }

    public override string ToString() => Count == 0 ? "set()" : "{" + string.Join(", ", this.Select(item => item.Repr())) + "}";

    // The interpreter installs a step callback for native iteration while a script runs.
    private static void Charge() => PyIterator.ChargeStep?.Invoke(0);

    private void Update(IEnumerable<PyValue> source)
    {
        if (ReferenceEquals(this, source)) return;
        foreach (var item in source)
        {
            Charge();
            Add(item);
        }
    }

    private void DifferenceUpdate(IEnumerable<PyValue> source)
    {
        if (ReferenceEquals(this, source)) { Clear(); return; }
        foreach (var item in source)
        {
            Charge();
            RemoveHashable(item);
        }
    }

    private PySet Intersect(IEnumerable<PyValue> source)
    {
        if (source is PySet other && Count < other.Count) return other.Intersect(this);
        var result = new PySet();
        foreach (var item in source)
        {
            Charge();
            if (ContainsHashable(item)) result.Add(item);
            if (result.Count == Count) break;
        }
        return result;
    }

    private void SymmetricDifferenceUpdate(PySet other)
    {
        if (ReferenceEquals(this, other)) { Clear(); return; }
        foreach (var item in other)
        {
            Charge();
            if (!Remove(item)) Add(item);
        }
    }

    private bool HasAll(IEnumerable<PyValue> source)
    {
        foreach (var item in source)
        {
            Charge();
            if (!ContainsHashable(item)) return false;
        }
        return true;
    }

    private bool IsDisjoint(IEnumerable<PyValue> source)
    {
        if (source is PySet other && Count < other.Count) return other.IsDisjoint(this);
        foreach (var item in source)
        {
            Charge();
            if (ContainsHashable(item)) return false;
        }
        return true;
    }

    internal bool IsSubsetOf(PySet other) => ReferenceEquals(this, other) || (Count <= other.Count && other.HasAll(this));
    internal bool SetEquals(PySet other) => Count == other.Count && IsSubsetOf(other);

    internal static bool IsSetOperator(string operation) => operation is "|" or "&" or "-" or "^";

    internal PySet Operate(string operation, PySet other, bool inPlace = false)
    {
        if (operation == "&")
        {
            if (inPlace && ReferenceEquals(this, other)) return this;
            var intersection = Intersect(other);
            if (!inPlace) return intersection;
            ReplaceWith(intersection);
            return this;
        }

        var result = inPlace ? this : new PySet(this);
        switch (operation)
        {
            case "|": result.Update(other); break;
            case "-": result.DifferenceUpdate(other); break;
            case "^": result.SymmetricDifferenceUpdate(other); break;
        }
        return result;
    }

    private void ReplaceWith(PySet result)
    {
        if (ReferenceEquals(this, result) || (Count == 0 && result.Count == 0)) return;
        _items = result._items;
        _version++;
    }

    internal PyValue GetAttribute(string name)
    {
        return name switch
        {
            "add" or "discard" or "remove" or "pop" or "clear" or "copy" or
            "union" or "intersection" or "difference" or "symmetric_difference" or
            "update" or "intersection_update" or "difference_update" or "symmetric_difference_update" or
            "issubset" or "issuperset" or "isdisjoint" => PyValue.From(new PyCallable(args => Invoke(name, args), name)),
            _ => throw new PythonException("AttributeError", $"'set' object has no attribute '{name}'")
        };
    }

    private PyValue Invoke(string name, IReadOnlyList<PyValue> args)
    {
        int? arity = name switch
        {
            "pop" or "clear" or "copy" => 0,
            "union" or "intersection" or "difference" or "update" or "intersection_update" or "difference_update" => null,
            _ => 1
        };
        if (arity is { } count && args.Count != count)
            throw new PythonException("TypeError", $"set.{name}() expected {count} argument(s), got {args.Count}");

        switch (name)
        {
            case "add": Add(args[0]); break;
            case "discard": Remove(args[0]); break;
            case "remove":
                if (!Remove(args[0])) throw PyExceptions.KeyError(args[0]);
                break;
            case "pop":
                if (Count == 0) throw PyExceptions.KeyError(PyValue.From("pop from an empty set"));
                var item = _items.First();
                Remove(item);
                return item;
            case "clear": Clear(); break;
            case "copy": return PyValue.From(new PySet(this));
            case "issubset": return PyValue.From(IsSubsetOf(AsSet(args[0])));
            case "issuperset":
                return PyValue.From(args[0].Data is PySet other ? other.IsSubsetOf(this) : HasAll(PyOperations.Iterate(args[0])));
            case "isdisjoint": return PyValue.From(IsDisjoint(PyOperations.Iterate(args[0])));
            case "symmetric_difference": return PyValue.From(Operate("^", AsSet(args[0])));
            case "symmetric_difference_update": SymmetricDifferenceUpdate(AsSet(args[0])); break;
            case "intersection":
            case "intersection_update":
                var intersection = this;
                foreach (var argument in args) intersection = intersection.Intersect(PyOperations.Iterate(argument));
                if (name == "intersection") return PyValue.From(ReferenceEquals(this, intersection) ? new PySet(this) : intersection);
                ReplaceWith(intersection);
                break;
            case "union":
            case "difference":
            case "update":
            case "difference_update":
                bool mutates = name is "update" or "difference_update";
                var result = mutates ? this : new PySet(this);
                foreach (var argument in args)
                {
                    var source = PyOperations.Iterate(argument);
                    if (name is "union" or "update") result.Update(source);
                    else result.DifferenceUpdate(source);
                }
                if (!mutates) return PyValue.From(result);
                break;
        }
        return PyValue.None;
    }

    private static PySet AsSet(PyValue value) => value.Data as PySet ?? new PySet(PyOperations.Iterate(value));
}
