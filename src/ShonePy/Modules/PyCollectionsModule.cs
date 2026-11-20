// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Modules;

internal static class PyCollectionsModule
{
    internal static PyDict CreateModule() => new()
    {
        ["Counter"] = PyValue.From(new PyCallable(args => CreateCounter(args, null), "Counter")
        {
            InvokeWithKeywords = CreateCounter
        })
    };

    private static PyValue CreateCounter(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords)
    {
        var counter = new PyCounter();
        counter.ChangeCounts("Counter", args, keywords, subtract: false);
        return PyValue.From(counter);
    }
}
