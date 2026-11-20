// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Modules;

/// <summary>Managed subset of Python's functools module.</summary>
public static class PyFunctoolsModule
{
    /// <summary>Creates the functools module dictionary.</summary>
    public static PyDict CreateModule()
    {
        var module = new PyDict();
        module[PyValue.From("reduce")] = PyValue.From(new PyCallable(Reduce, "reduce")
        {
            InvokeWithKeywords = (args, keywords) =>
            {
                if (args.Count != 2 || keywords.Count != 1 || !keywords.TryGetValue("initial", out var initial))
                    throw new PythonException("TypeError", "reduce() expects a function and iterable as positional arguments and an optional initial value");
                return Reduce([args[0], args[1], initial]);
            }
        });
        return module;
    }

    private static PyValue Reduce(IReadOnlyList<PyValue> args)
    {
        if (args.Count is < 2 or > 3)
            throw new PythonException("TypeError", $"reduce() takes 2 or 3 arguments ({args.Count} given)");
        PyValue accumulator;
        using var iterator = PyOperations.Iterate(args[1]).GetEnumerator();
        if (args.Count == 3)
            accumulator = args[2];
        else if (iterator.MoveNext())
            accumulator = iterator.Current;
        else
            throw new PythonException("TypeError", "reduce() of empty iterable with no initial value");

        while (iterator.MoveNext())
            accumulator = args[0].Call([accumulator, iterator.Current]);
        return accumulator;
    }
}
