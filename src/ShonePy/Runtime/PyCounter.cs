// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;

namespace ShonePy;

internal sealed class PyCounter : PyDict
{
    internal override PyValue MissingValue(PyValue key) => PyValue.From(0);

    internal PyCallable? GetMethod(string name) => name switch
    {
        "update" => ChangeMethod(name, subtract: false),
        "subtract" => ChangeMethod(name, subtract: true),
        "most_common" => new PyCallable(args => MostCommon(args, null), name) { InvokeWithKeywords = MostCommon },
        "elements" => NoArgMethod(name, () => PyValue.From(PyIterator.FromEnumerable("counter_elements", Elements()))),
        "total" => NoArgMethod(name, () => this.Aggregate(PyValue.From(0), (sum, pair) => PyOperations.Binary(sum, "+", pair.Value))),
        "copy" => NoArgMethod(name, Copy),
        "get" => new PyCallable(args =>
        {
            if (args.Count is < 1 or > 2) throw new PythonException("TypeError", "get() expected 1 or 2 arguments");
            return TryGetValue(args[0], out PyValue value) ? value : args.Count == 2 ? args[1] : PyValue.None;
        }, name),
        _ => null
    };

    internal PyValue ChangeCounts(string name, IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords, bool subtract)
    {
        if (args.Count > 1) throw new PythonException("TypeError", $"{name}() expected at most one positional argument");
        if (args.Count == 1 && args[0].Type != PyType.None)
        {
            if (args[0].Data is PyDict mapping)
            {
                bool copyValues = !subtract && Count == 0;
                var entries = ReferenceEquals(this, mapping) ? mapping.ToArray() : (IEnumerable<KeyValuePair<PyValue, PyValue>>)mapping;
                foreach (var pair in entries)
                    this[pair.Key] = copyValues ? pair.Value : ChangeMappingCount(pair.Key, pair.Value, subtract);
            }
            else
            {
                foreach (PyValue key in PyOperations.GetIterator(args[0]))
                    this[key] = PyOperations.Binary(this[key], subtract ? "-" : "+", PyValue.From(1));
            }
        }
        if (keywords is not null)
        {
            bool copyValues = !subtract && Count == 0;
            foreach (var pair in keywords)
                this[pair.Key] = copyValues ? pair.Value : ChangeMappingCount(pair.Key, pair.Value, subtract);
        }
        return PyValue.None;
    }

    private PyCallable ChangeMethod(string name, bool subtract) => new(args => ChangeCounts(name, args, null, subtract), name)
    {
        InvokeWithKeywords = (args, keywords) => ChangeCounts(name, args, keywords, subtract)
    };

    private PyValue ChangeMappingCount(PyValue key, PyValue count, bool subtract) => subtract
        ? PyOperations.Binary(this[key], "-", count)
        : PyOperations.Binary(count, "+", this[key]);

    private PyValue MostCommon(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords)
    {
        if (args.Count > 1) throw new PythonException("TypeError", "most_common() expected at most one positional argument");
        PyValue limit = args.Count == 1 ? args[0] : PyValue.None;
        if (keywords is not null)
        {
            foreach (var pair in keywords)
            {
                if (pair.Key != "n") throw new PythonException("TypeError", $"most_common() got an unexpected keyword argument '{pair.Key}'");
                if (args.Count == 1) throw new PythonException("TypeError", "most_common() got multiple values for argument 'n'");
                limit = pair.Value;
            }
        }

        int count = Count;
        if (limit.Type != PyType.None)
        {
            BigInt requested = AsRepeatCount(limit);
            if (requested <= BigInt.Zero) return PyValue.From(new PyList());
            if (requested < new BigInt(count)) count = (int)requested;
        }
        var pairs = CommonFirst().Take(count).Select(pair => PyValue.From(new PyTuple(pair.Key, pair.Value)));
        return PyValue.From(new PyList(pairs));
    }

    private KeyValuePair<PyValue, PyValue>[] CommonFirst()
    {
        try
        {
            // OrderByDescending is stable, retaining insertion order for equal counts.
            return this.OrderByDescending(pair => pair.Value, Comparer<PyValue>.Create((left, right) =>
                PyOperations.Compare(left, "<", right) ? -1 : PyOperations.Compare(right, "<", left) ? 1 : 0)).ToArray();
        }
        catch (InvalidOperationException error) when (error.InnerException is PythonException python)
        {
            throw new PythonException(python.ErrorName, python.Message);
        }
    }

    private IEnumerable<PyValue> Elements()
    {
        var keys = this.Select(pair => pair.Key).ToArray();
        foreach (PyValue key in keys)
        {
            if (Count != keys.Length || !TryGetValue(key, out PyValue count))
                throw new PythonException("RuntimeError", "dictionary changed size during iteration");
            BigInt repeats = AsRepeatCount(count);
            if (repeats < new BigInt(long.MinValue) || repeats > new BigInt(long.MaxValue))
                throw new PythonException("OverflowError", "Python int too large to convert to C ssize_t");
            for (long remaining = (long)repeats; remaining > 0; remaining--)
                yield return key;
        }
        if (Count != keys.Length) throw new PythonException("RuntimeError", "dictionary changed size during iteration");
    }

    private PyValue Copy()
    {
        var copy = new PyCounter();
        foreach (var pair in this) copy[pair.Key] = pair.Value;
        return PyValue.From(copy);
    }

    private static BigInt AsRepeatCount(PyValue value) => value.Type switch
    {
        PyType.Integer => (BigInt)value.Data!,
        PyType.Boolean => value.IsTruthy() ? BigInt.One : BigInt.Zero,
        _ => throw new PythonException("TypeError", $"'{PyOperations.TypeName(value)}' object cannot be interpreted as an integer")
    };

    private static PyCallable NoArgMethod(string name, Func<PyValue> body) => new(args =>
    {
        if (args.Count != 0) throw new PythonException("TypeError", $"{name}() takes no arguments");
        return body();
    }, name);

    public override string ToString()
    {
        if (Count == 0) return "Counter()";
        IEnumerable<KeyValuePair<PyValue, PyValue>> pairs;
        try { pairs = CommonFirst(); }
        catch (PythonException error) when (error.ErrorName == "TypeError") { pairs = this; }
        return "Counter({" + string.Join(", ", pairs.Select(pair => pair.Key.Repr() + ": " + pair.Value.Repr())) + "})";
    }
}
