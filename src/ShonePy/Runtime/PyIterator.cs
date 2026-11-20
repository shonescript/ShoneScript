// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections;

namespace ShonePy;

internal sealed class PyIterator : IEnumerable<PyValue>
{
    // A cursor belongs to the Python value. Each .NET enumerator borrows it, so
    // disposing a foreach loop after break leaves the cursor available to Python.
    private Func<PyValue?>? _pull;
    private bool _advancing;

    [ThreadStatic] internal static Action<int>? ChargeStep;
    [ThreadStatic] private static int _depth;

    internal string Name { get; }
    private Func<(PyValue? Item, PyValue Result)>? _resumeGenerator;

    internal PyIterator(string name, Func<PyValue?> pull) => (Name, _pull) = (name, pull);
    private PyIterator(Func<(PyValue? Item, PyValue Result)> resume) =>
        (Name, _resumeGenerator) = ("generator", resume);

    internal static PyIterator ForGenerator(Func<(PyValue? Item, PyValue Result)> resume) => new(resume);

    internal PyValue? Next() => Next(out _);

    internal PyValue? Next(out PyValue result)
    {
        result = PyValue.None;
        if (_pull is null && _resumeGenerator is null) return null;
        if (_advancing) throw new PythonException("ValueError", _resumeGenerator is null ? "iterator is already executing" : "generator already executing");

        _depth++;
        try
        {
            ChargeStep?.Invoke(_depth);
            _advancing = true;
            if (_resumeGenerator is not { } resume)
            {
                PyValue? item = _pull!();
                if (item is null) _pull = null;
                return item;
            }

            try
            {
                (PyValue? item, result) = resume();
                if (item is null) ExhaustGenerator();
                return item;
            }
            catch
            {
                ExhaustGenerator();
                throw;
            }
        }
        finally
        {
            _advancing = false;
            _depth--;
        }
    }

    private void ExhaustGenerator()
    {
        _resumeGenerator = null;
    }

    internal static PyIterator FromEnumerable(string name, IEnumerable<PyValue> items)
    {
        var cursor = items.GetEnumerator();
        return new PyIterator(name, () =>
        {
            if (cursor.MoveNext()) return cursor.Current;
            cursor.Dispose();
            return null;
        });
    }

    public IEnumerator<PyValue> GetEnumerator()
    {
        while (Next() is { } value) yield return value;
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"<{Name} object>";
}
