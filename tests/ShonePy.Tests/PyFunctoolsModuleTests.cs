// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy;
using ShonePy.Modules;

namespace ShonePy.Tests;

public sealed class PyFunctoolsModuleTests
{
    [Fact]
    public void ReduceFoldsValuesAndSupportsInitializer()
    {
        var module = PyFunctoolsModule.CreateModule();
        var add = PyValue.From(new PyCallable(args => PyOperations.Binary(args[0], "+", args[1]), "add"));

        Assert.Equal(PyValue.From(10), Call(module, add, PyValue.From(new PyList([1, 2, 3, 4]))));
        Assert.Equal(PyValue.From(16), Call(module, add, PyValue.From(new PyTuple(1, 2, 3)), PyValue.From(10)));
    }

    [Fact]
    public void ReduceReturnsSingleElementAndInitializerForEmptyInput()
    {
        var module = PyFunctoolsModule.CreateModule();
        var mustNotRun = PyValue.From(new PyCallable(_ => throw new InvalidOperationException(), "must_not_run"));

        Assert.Equal(PyValue.From(7), Call(module, mustNotRun, PyValue.From(new PyList([7]))));
        Assert.Equal(PyValue.From(9), Call(module, mustNotRun, PyValue.From(new PyList()), PyValue.From(9)));
    }

    [Fact]
    public void ReduceMapsArgumentAndTypeErrors()
    {
        var module = PyFunctoolsModule.CreateModule();
        var add = PyValue.From(new PyCallable(args => PyOperations.Binary(args[0], "+", args[1]), "add"));

        var emptyError = Assert.Throws<PythonException>(() => Call(module, add, PyValue.From(new PyList())));
        Assert.Equal("TypeError", emptyError.ErrorName);
        Assert.Contains("empty iterable", emptyError.Message);

        var callableError = Assert.Throws<PythonException>(() => Call(module, PyValue.From(1), PyValue.From(new PyList([1, 2]))));
        Assert.Equal("TypeError", callableError.ErrorName);
        Assert.Equal(PyValue.From(7), Call(module, PyValue.From(1), PyValue.From(new PyList([7]))));

        var arityError = Assert.Throws<PythonException>(() => module[PyValue.From("reduce")].Call([add]));
        Assert.Equal("TypeError", arityError.ErrorName);
    }

    [Fact]
    public void ReduceSupportsTheInitialKeyword()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyValue.From(42), engine.Evaluate("from functools import reduce\ndef add(a, b): return a + b\nreduce(add, [2], initial=40)"));
    }

    private static PyValue Call(PyDict module, params PyValue[] args) =>
        module[PyValue.From("reduce")].Call(args);
}
