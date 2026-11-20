// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Text;

namespace ShonePy.Modules;

internal static class PyTypingModule
{
    public static PyDict CreateModule()
    {
        var module = new PyDict();
        module[PyValue.From("cast")] = PyValue.From(new PyCallable(Cast, "cast")
        {
            InvokeWithKeywords = CastWithKeywords
        });
        module[PyValue.From("assert_type")] = PyValue.From(new PyCallable(AssertType, "assert_type"));
        module[PyValue.From("assert_never")] = PyValue.From(new PyCallable(AssertNever, "assert_never"));
        return module;
    }

    private static PyValue Cast(IReadOnlyList<PyValue> args)
    {
        RequireArity("cast", args, 2);
        return args[1];
    }

    private static PyValue CastWithKeywords(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue> keywords)
    {
        foreach (string name in keywords.Keys)
            if (name is not ("typ" or "val"))
                throw new PythonException("TypeError", $"cast() got an unexpected keyword argument '{name}'");

        if (args.Count > 2)
            RequireArity("cast", args, 2);
        if (args.Count >= 1 && keywords.ContainsKey("typ"))
            throw new PythonException("TypeError", "cast() got multiple values for argument 'typ'");
        if (args.Count == 2 && keywords.ContainsKey("val"))
            throw new PythonException("TypeError", "cast() got multiple values for argument 'val'");
        if (args.Count == 0 && !keywords.ContainsKey("typ"))
            throw new PythonException("TypeError", "cast() missing required argument 'typ'");
        if (args.Count == 2)
            return args[1];
        if (!keywords.TryGetValue("val", out PyValue value))
            throw new PythonException("TypeError", "cast() missing required argument 'val'");
        return value;
    }

    private static PyValue AssertType(IReadOnlyList<PyValue> args)
    {
        RequireArity("assert_type", args, 2);
        return args[0];
    }

    private static PyValue AssertNever(IReadOnlyList<PyValue> args)
    {
        RequireArity("assert_never", args, 1);
        const int maxReprLength = 100;
        string repr = args[0].Repr();
        int count = 0;
        int offset = 0;
        foreach (Rune rune in repr.EnumerateRunes())
        {
            if (count++ == maxReprLength)
            {
                repr = repr[..offset] + "...";
                break;
            }
            offset += rune.Utf16SequenceLength;
        }
        throw new PythonException("AssertionError", $"Expected code to be unreachable, but got: {repr}");
    }

    private static void RequireArity(string name, IReadOnlyList<PyValue> args, int count)
    {
        if (args.Count != count)
            throw new PythonException("TypeError", $"{name}() takes exactly {count} argument(s) ({args.Count} given)");
    }
}
