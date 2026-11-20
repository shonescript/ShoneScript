// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy;
using ShonePy.Modules;

namespace ShonePy.Tests;

public sealed class PyRegexModuleTests
{
    [Fact]
    public void SearchProvidesPythonStyleMatchOperations()
    {
        var module = PyRegexModule.CreateModule();
        PyValue match = Call(module, "search", "([a-z]+)-(?P<digits>\\d+)", "id=abc-42!");
        Assert.Equal("abc-42", Get(match, "group").Call([]).Data);
        Assert.Equal("abc", Get(match, "group").Call([PyValue.From(1)]).Data);
        Assert.Equal("42", Get(match, "group").Call([PyValue.From(2)]).Data);
        Assert.Equal("3", Get(match, "start").Call([]).ToPythonString());
        var groupdict = (PyDict)Get(match, "groupdict").Call([]).Data!;
        Assert.Equal("42", groupdict[PyValue.From("digits")].Data);
        Assert.Equal(PyValue.None, Call(module, "match", "abc", "xabc"));
        Assert.Equal("abc", Get(Call(module, "fullmatch", "abc", "abc"), "group").Call([]).Data);
        Assert.NotEqual(PyValue.None, Call(module, "fullmatch", "a|ab", "ab"));
    }

    [Fact]
    public void FindallAndSubstitutionHandleCapturesAndCount()
    {
        var module = PyRegexModule.CreateModule();
        var found = (PyList)Call(module, "findall", "(\\w+)=(\\d+)", "a=1 b=20").Data!;
        Assert.Equal(2, found.Count);
        Assert.Equal("a", ((PyTuple)found[0].Data!)[0].Data);
        Assert.Equal("20", ((PyTuple)found[1].Data!)[1].Data);
        Assert.Equal("x=1 b=20", Call(module, "sub", "a=(\\d+)", "x=\\1", "a=1 b=20", 1).Data);
    }

    [Fact]
    public void FlagsAndInvalidPatternsAreMapped()
    {
        var module = PyRegexModule.CreateModule();
        var match = Call(module, "search", "abc", "ABC", Get(PyValue.From(module), "IGNORECASE"));
        Assert.NotEqual(PyValue.None, match);
        var error = Assert.Throws<PythonException>(() => Call(module, "search", "[", "x"));
        Assert.Equal("re.error", error.ErrorName);
    }

    private static PyValue Call(PyDict module, string name, params object[] values) =>
        module[PyValue.From(name)].Call(values.Select(PyValue.From).ToArray());
    private static PyValue Get(PyValue value, string key) => ((PyDict)value.Data!)[PyValue.From(key)];
}
