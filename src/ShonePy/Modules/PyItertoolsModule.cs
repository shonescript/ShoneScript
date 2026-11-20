// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;

namespace ShonePy.Modules;

internal static class PyItertoolsModule
{
    internal static PyDict CreateModule()
    {
        var module = new PyDict();
        Add("chain", Chain);
        Add("islice", Islice);
        Add("repeat", Repeat, ["object", "times"]);
        Add("accumulate", Accumulate, ["iterable", "func", "initial"], maximumPositional: 2);
        Add("count", Count, ["start", "step"]);
        return module;

        void Add(string name, Func<IReadOnlyList<PyValue>, PyValue> invoke, string[]? parameters = null,
            int? maximumPositional = null)
        {
            void CheckPositional(IReadOnlyList<PyValue> args)
            {
                if (args.Count > maximumPositional)
                    throw new PythonException("TypeError", $"{name}() takes at most {maximumPositional} positional arguments");
            }

            module[PyValue.From(name)] = PyValue.From(new PyCallable(args =>
            {
                CheckPositional(args);
                return invoke(args);
            }, name)
            {
                InvokeWithKeywords = parameters is null ? null : (args, keywords) =>
                {
                    CheckPositional(args);
                    return invoke(BindKeywords(name, parameters, args, keywords));
                }
            });
        }
    }

    private static PyValue Chain(IReadOnlyList<PyValue> args)
    {
        PyIterator? sources = PyIterator.FromEnumerable("tuple_iterator", args.ToArray());
        PyIterator? current = null;
        return PyValue.From(new PyIterator("chain", () =>
        {
            while (true)
            {
                if (current is not null && current.Next() is { } value) return value;
                current = null;
                if (sources?.Next() is not { } source) return null;
                try { current = PyOperations.GetIterator(source); }
                catch (PythonException)
                {
                    sources = null;
                    throw;
                }
            }
        }));
    }

    private static PyValue Islice(IReadOnlyList<PyValue> args)
    {
        CheckArity("islice", args, 2, 4);
        long start = args.Count == 2 ? 0 : SliceIndex(args[1], "start") ?? 0;
        long? stop = SliceIndex(args[args.Count == 2 ? 1 : 2], "stop");
        long step = args.Count == 4 ? SliceIndex(args[3], "step", minimum: 1) ?? 1 : 1;
        var source = PyOperations.GetIterator(args[0]);
        long skip = start;
        // A fully consumed slice advances a shared input by max(start, stop), regardless of step.
        long? remaining = stop is long end ? Math.Max(start, end) : null;
        return PyValue.From(new PyIterator("islice", () =>
        {
            while (remaining != 0)
            {
                if (source.Next() is not { } value) return null;
                if (remaining.HasValue) remaining--;
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                skip = step - 1;
                return value;
            }
            return null;
        }));
    }

    private static PyValue Repeat(IReadOnlyList<PyValue> args)
    {
        CheckArity("repeat", args, 1, 2);
        PyValue value = args[0];
        long? remaining = null;
        if (args.Count == 2)
        {
            if (!TryInteger(args[1], out var times))
                throw new PythonException("TypeError", $"'{PyOperations.TypeName(args[1])}' object cannot be interpreted as an integer");
            if (times < new BigInt(long.MinValue) || times > new BigInt(long.MaxValue))
                throw new PythonException("OverflowError", "Python int too large to convert to C ssize_t");
            remaining = Math.Max(0, (long)times);
        }
        return PyValue.From(new PyIterator("repeat", () =>
        {
            if (remaining == 0) return null;
            if (remaining.HasValue) remaining--;
            return value;
        }));
    }

    private static PyValue Accumulate(IReadOnlyList<PyValue> args)
    {
        CheckArity("accumulate", args, 1, 3);
        var source = PyOperations.GetIterator(args[0]);
        PyValue func = args.Count >= 2 ? args[1] : PyValue.None;
        PyValue initial = args.Count == 3 ? args[2] : PyValue.None;
        bool hasInitial = initial.Type != PyType.None;
        PyValue? total = null;
        return PyValue.From(new PyIterator("accumulate", () =>
        {
            if (hasInitial)
            {
                hasInitial = false;
                return total = initial;
            }
            if (source.Next() is not { } value) return null;
            return total = total is not { } previous ? value
                : func.Type == PyType.None ? PyOperations.Binary(previous, "+", value)
                : func.Call([previous, value]);
        }));
    }

    private static PyValue Count(IReadOnlyList<PyValue> args)
    {
        CheckArity("count", args, 0, 2);
        PyValue current = args.Count > 0 ? args[0] : PyValue.From(0);
        PyValue step = args.Count == 2 ? args[1] : PyValue.From(1);
        if (!IsNumeric(current) || !IsNumeric(step))
            throw new PythonException("TypeError", "a number is required");
        bool started = false;
        return PyValue.From(new PyIterator("count", () =>
        {
            if (started) current = PyOperations.Binary(current, "+", step);
            started = true;
            return current;
        }));
    }

    private static IReadOnlyList<PyValue> BindKeywords(string name, string[] parameters,
        IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue> keywords)
    {
        if (args.Count > parameters.Length)
            throw new PythonException("TypeError", $"{name}() takes at most {parameters.Length} arguments");
        var values = new PyValue?[parameters.Length];
        for (int i = 0; i < args.Count; i++) values[i] = args[i];
        foreach (var (key, value) in keywords)
        {
            int index = Array.IndexOf(parameters, key);
            if (index < 0) throw new PythonException("TypeError", $"{name}() got an unexpected keyword argument '{key}'");
            if (values[index].HasValue) throw new PythonException("TypeError", $"{name}() got multiple values for argument '{key}'");
            values[index] = value;
        }
        if (name != "count" && !values[0].HasValue)
            throw new PythonException("TypeError", $"{name}() missing required argument '{parameters[0]}'");
        int length = Array.FindLastIndex(values, value => value.HasValue) + 1;
        var result = new PyValue[length];
        for (int i = 0; i < length; i++)
            result[i] = values[i] ?? (name == "count" ? PyValue.From(0) : PyValue.None);
        return result;
    }

    private static bool IsNumeric(PyValue value) => value.Type is PyType.Integer or PyType.Boolean or PyType.Float;

    private static bool TryInteger(PyValue value, out BigInt integer)
    {
        if (value.Type == PyType.Integer)
        {
            integer = (BigInt)value.Data!;
            return true;
        }
        integer = value.IsTruthy() ? BigInt.One : BigInt.Zero;
        return value.Type == PyType.Boolean;
    }

    private static long? SliceIndex(PyValue value, string parameter, int minimum = 0)
    {
        if (value.Type == PyType.None) return null;
        if (TryInteger(value, out var integer) && integer >= new BigInt(minimum) && integer <= new BigInt(long.MaxValue))
            return (long)integer;
        throw new PythonException("ValueError", $"{parameter} argument for islice() must be None or an integer: {minimum} <= x <= sys.maxsize");
    }

    private static void CheckArity(string name, IReadOnlyList<PyValue> args, int minimum, int maximum)
    {
        if (args.Count < minimum || args.Count > maximum)
            throw new PythonException("TypeError", $"{name}() takes {minimum} to {maximum} arguments ({args.Count} given)");
    }
}
