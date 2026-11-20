// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Runtime;

internal sealed class PyClass
{
    internal static PyClass Object { get; } = new("object", "builtins", []);

    private readonly Dictionary<string, PyValue> _members = new(StringComparer.Ordinal);
    internal string Name { get; }
    internal string Module { get; }
    internal IReadOnlyList<PyClass> Bases { get; }
    internal IReadOnlyList<PyClass> Mro { get; }

    internal PyClass(string name, string module, IReadOnlyList<PyClass> bases)
    {
        Name = name;
        Module = module;
        Bases = bases;
        if (bases.Distinct().Count() != bases.Count)
            throw new PythonException("TypeError", "duplicate base class");

        // C3 linearization preserves local base order and each parent's MRO.
        var order = new List<PyClass> { this };
        var sequences = bases.Select(b => new Queue<PyClass>(b.Mro)).ToList();
        sequences.Add(new Queue<PyClass>(bases));
        while (sequences.Any(sequence => sequence.Count > 0))
        {
            var next = sequences.Where(sequence => sequence.Count > 0).Select(sequence => sequence.Peek())
                .FirstOrDefault(candidate => !sequences.Any(sequence => sequence.Skip(1).Contains(candidate)));
            if (next is null)
                throw new PythonException("TypeError", "cannot create a consistent method resolution order (MRO)");
            order.Add(next);
            foreach (var sequence in sequences)
                if (sequence.TryPeek(out var head) && head == next) sequence.Dequeue();
        }
        Mro = order;
    }

    internal void InitializeMembers(IEnumerable<KeyValuePair<string, PyValue>> members)
    {
        foreach (var (name, value) in members) _members[name] = value;
    }

    internal void SetAttribute(string name, PyValue value)
    {
        if (ReferenceEquals(this, Object) || name is "__bases__" or "__mro__" or "__name__")
            throw new PythonException("TypeError", $"cannot set attribute '{name}' on '{Name}'");
        _members[name] = value;
    }

    internal void DeleteAttribute(string name)
    {
        if (this == Object || name is "__bases__" or "__mro__" or "__name__" or "__module__")
            throw new PythonException("TypeError", $"cannot delete attribute '{name}' on '{Name}'");
        if (!_members.Remove(name))
            throw new PythonException("AttributeError", $"type object '{Name}' has no attribute '{name}'");
    }

    internal bool TryLookup(string name, out PyValue value, int start = 0, Action? chargeStep = null)
    {
        for (int i = start; i < Mro.Count; i++)
        {
            chargeStep?.Invoke();
            if (Mro[i]._members.TryGetValue(name, out value)) return true;
        }
        value = PyValue.None;
        return false;
    }

    internal PyValue GetAttribute(string name) => name switch
    {
        "__name__" => PyValue.From(Name),
        "__bases__" => PyValue.From(new PyTuple(Bases.Select(PyValue.From))),
        "__mro__" => PyValue.From(new PyTuple(Mro.Select(PyValue.From))),
        _ when TryLookup(name, out var value) => BindMember(value, null),
        "__module__" => PyValue.From(Module),
        _ => throw new PythonException("AttributeError", $"type object '{Name}' has no attribute '{name}'")
    };

    internal PyValue BindMember(PyValue value, PyInstance? instance) => value.Data switch
    {
        PyDescriptor descriptor => descriptor.Bind(instance, this),
        PyCallable { IsPythonFunction: true } when instance is not null => PyMethodDescriptor.BindMethod(value, PyValue.From(instance)),
        _ => value
    };

    internal PyValue Construct(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords = null)
    {
        var instance = new PyInstance(this);
        if (instance.TryGetSpecial("__init__", out var init))
        {
            PyValue result = init.Call(args, keywords);
            if (result.Type != PyType.None)
                throw new PythonException("TypeError", $"__init__() should return None, not '{PyOperations.TypeName(result)}'");
        }
        else if (args.Count > 0 || keywords is { Count: > 0 })
        {
            throw new PythonException("TypeError", $"{Name}() takes no arguments");
        }
        return PyValue.From(instance);
    }

    public override string ToString() => this == Object ? "<class 'object'>" : $"<class '{Module}.{Name}'>";
}

internal sealed class PyInstance(PyClass cls)
{
    private readonly Dictionary<string, PyValue> _members = new(StringComparer.Ordinal);
    internal PyClass Class { get; } = cls;

    internal void SetAttribute(string name, PyValue value)
    {
        if (Class == PyClass.Object || name == "__class__")
            throw new PythonException("AttributeError", $"'{Class.Name}' object does not support assignment to '{name}'");
        if (Class.TryLookup(name, out var member) && member.Data is PyProperty property)
            property.Set(this, value);
        else
            _members[name] = value;
    }

    internal void DeleteAttribute(string name)
    {
        if (name != "__class__" && Class.TryLookup(name, out var member) && member.Data is PyProperty property)
            property.Delete(this);
        else if (!_members.Remove(name))
            throw new PythonException("AttributeError", $"'{Class.Name}' object has no attribute '{name}'");
    }

    internal PyValue GetAttribute(string name)
    {
        if (name == "__class__") return PyValue.From(Class);
        bool found = Class.TryLookup(name, out var member);
        if (member.Data is PyProperty property) return property.Bind(this, Class);
        if (_members.TryGetValue(name, out var value)) return value;
        if (found) return Class.BindMember(member, this);
        throw new PythonException("AttributeError", $"'{Class.Name}' object has no attribute '{name}'");
    }

    internal bool TryGetSpecial(string name, out PyValue value)
    {
        if (!Class.TryLookup(name, out value)) return false;
        value = Class.BindMember(value, this);
        return true;
    }

    public override string ToString() => $"<{Class.Module}.{Class.Name} object at 0x{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this):x}>";
}
