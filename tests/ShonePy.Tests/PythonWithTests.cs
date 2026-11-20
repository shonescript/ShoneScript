// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonWithTests
{
    [Fact]
    public void EntersLeftToRightAndExitsInReverseOrder()
    {
        var result = new PythonEngine().Evaluate("""
            events = []
            def manager(value):
                events.append(('make', value))
                def enter():
                    events.append(('enter', value))
                    return (value, value + 1)
                def exit(kind, error, traceback):
                    events.append(('exit', value, kind, error, traceback))
                return {'__enter__': enter, '__exit__': exit}
            with (
                manager(1) as (a, b),
                manager(b) as pair,
            ):
                events.append((a, b, pair))
            events
            """);

        Assert.Equal("[('make', 1), ('enter', 1), ('make', 2), ('enter', 2), (1, 2, (2, 3)), ('exit', 2, None, None, None), ('exit', 1, None, None, None)]", result.Repr());
    }

    [Fact]
    public void PassesExceptionIdentityAndOnlyExitsSuccessfullyEnteredManagers()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            original = ValueError('entry failed')
            def enter():
                trace.append('enter')
            def fail():
                raise original
            def exit(kind, error, traceback):
                trace.append((kind is ValueError, error is original, traceback is None))
                return 1
            def wrong(kind, error, traceback):
                trace.append('wrong')
            outer = {'__enter__': enter, '__exit__': exit}
            inner = {'__enter__': fail, '__exit__': wrong}
            with outer, inner:
                trace.append('wrong')
            trace.append('resumed')
            trace
            """);

        Assert.Equal("['enter', (True, True, True), 'resumed']", result.Repr());
    }

    [Fact]
    public void ExitCanReraiseOrReplaceThePendingException()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            original = ValueError('first')
            def enter():
                return (1,)
            def reraiser(kind, error, traceback):
                raise
            def replace(kind, error, traceback):
                trace.append(kind is ValueError)
                raise KeyError('replacement')
            outer = {'__enter__': enter, '__exit__': replace}
            inner = {'__enter__': enter, '__exit__': reraiser}
            try:
                with outer, inner: raise original
            except KeyError as error:
                trace.append(error.args)
            try:
                with inner as (x, y): pass
            except ValueError:
                trace.append('unpack failed')
            trace
            """);

        Assert.Equal("[True, ('replacement',), 'unpack failed']", result.Repr());
    }

    [Fact]
    public void CallsExitForReturnBreakAndContinueAndUsesLexicalScope()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            def enter(): return 7
            def exit(kind, error, traceback):
                trace.append(kind is None and error is None and traceback is None)
                return True
            manager = {'__enter__': enter, '__exit__': exit}
            def run():
                for i in [1, 2]:
                    with (manager) as value:
                        if i == 1: continue
                        break
                else:
                    trace.append('wrong')
                with manager:
                    def read():
                        nonlocal value
                        return value
                    return read()
            trace.append(run())
            trace
            """);

        Assert.Equal("[True, True, True, 7]", result.Repr());
    }

    [Fact]
    public void LooksUpExitBeforeEnteringAndUsesTheCachedMethod()
    {
        var result = new PythonEngine().Evaluate("""
            trace = []
            def exit(kind, error, traceback): trace.append('exit')
            def new_exit(kind, error, traceback): trace.append('new exit')
            def enter():
                trace.append('enter')
                manager['__exit__'] = new_exit
            manager = {'__enter__': enter}
            try:
                with manager: pass
            except TypeError:
                trace.append('missing exit')
            manager['__exit__'] = exit
            with manager: pass
            trace
            """);

        Assert.Equal("['missing exit', 'enter', 'exit']", result.Repr());
    }

    [Fact]
    public void HostCanSupplyContextManagerCallbacks()
    {
        var engine = new PythonEngine();
        IReadOnlyList<PyValue>? exitArguments = null;
        var manager = new PyDict
        {
            ["__enter__"] = PyValue.From(new PyCallable(args =>
            {
                Assert.Empty(args);
                return 42;
            })),
            ["__exit__"] = PyValue.From(new PyCallable(args =>
            {
                exitArguments = args;
                return PyValue.None;
            }))
        };
        engine.SetGlobal("factory", PyValue.From(new PyCallable(_ => PyValue.From(manager))));

        var result = engine.Evaluate("with (factory)() as value: pass\nvalue");

        Assert.Equal("42", result.Repr());
        Assert.Equal(new[] { PyValue.None, PyValue.None, PyValue.None }, exitArguments);
    }

    [Fact]
    public void InterpreterBudgetAbortsWithoutRunningExit()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 30 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            events = []
            def enter(): events.append('entered')
            def exit(kind, error, traceback): events.append('exited')
            manager = {'__enter__': enter, '__exit__': exit}
            with manager:
                while True: pass
            """));

        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Contains("maximum execution steps exceeded", error.Message);
        Assert.Equal("['entered']", engine.GetGlobal("events").Repr());
    }

    [Theory]
    [InlineData("with cm as 2: pass")]
    [InlineData("with cm,: pass")]
    [InlineData("async with cm: pass")]
    public void RejectsInvalidOrUnsupportedSyntax(string source)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().ParseScript(source));
        Assert.Equal("SyntaxError", error.ErrorName);
    }
}
