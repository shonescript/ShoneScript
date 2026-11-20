// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonGeneratorTests
{
    [Fact]
    public void RunsLazilyWithIndependentLocalsAndSharedClosure()
    {
        var engine = new PythonEngine();
        Assert.Equal("[]", engine.Evaluate("""
            trace = []
            def factory():
                counter = 0
                def generate(start):
                    nonlocal counter
                    counter += 1
                    trace.append(start)
                    x = yield start + counter
                    yield x, counter
                return generate
            make = factory()
            a = make(10)
            b = make(20)
            trace
            """).Repr());
        Assert.Equal("(True, 11, 22, (None, 2), [(None, 2)], [10, 20], 99)", engine.Evaluate("""
            (iter(a) is a, next(a), next(b), a.__next__(), list(b), trace, next(a, 99))
            """).Repr());
    }

    [Fact]
    public void SupportsLoopControlDelegationAndOneShotReturnValues()
    {
        Assert.Equal("([1, 3, 5, 6, 7, 8], (42, (42,)), (None, ()))", new PythonEngine().Evaluate("""
            def inner():
                for x in range(4):
                    if x % 2 == 0: continue
                    yield x
                else: yield 5
                i = 5
                while True:
                    i += 1
                    yield i
                    if i == 7: break
                else: yield -1
                return 42
            def outer():
                result = yield from inner()
                yield from [8]
                return result
            iterator = outer()
            values = [next(iterator) for _ in range(6)]
            try: next(iterator)
            except StopIteration as error: first = (error.value, error.args)
            try: next(iterator)
            except StopIteration as error: second = (error.value, error.args)
            (values, first, second)
            """).Repr());
    }

    [Fact]
    public void AbortedIterationLeavesTheCursorSuspended()
    {
        Assert.Equal("(0, True, [1, 2])", new PythonEngine().Evaluate("""
            def generate():
                for i in range(3): yield i
            iterator = generate()
            for item in iterator: break
            (item, iterator.__iter__() is iterator, list(iterator))
            """).Repr());
    }

    [Fact]
    public void ClassifiesUnreachableYieldWithoutClassifyingItsEnclosingFunction()
    {
        Assert.Equal("([], 42)", new PythonEngine().Evaluate("""
            def factory():
                def never():
                    if False: yield 1
                return never(), 42
            iterator, result = factory()
            (list(iterator), result)
            """).Repr());
    }

    [Theory]
    [InlineData("yield 1")]
    [InlineData("x = (yield 1)")]
    [InlineData("def g():\n    class C:\n        yield 1")]
    [InlineData("def g():\n    [(yield 1) for x in range(2)]")]
    [InlineData("def yield(): pass")]
    public void InvalidYieldPositionsFailAtParseTime(string source)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().ParseScript(source));
        Assert.Equal("SyntaxError", error.ErrorName);
    }

    [Fact]
    public void BindsArgumentsBeforeFirstResume()
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().Evaluate("def g(value): yield value\ng()"));
        Assert.Equal("TypeError", error.ErrorName);
    }

    [Fact]
    public void ConvertsEscapingStopIterationAndExhaustsAfterFailure()
    {
        Assert.Equal("(1, (2, 2), 3)", new PythonEngine().Evaluate("""
            def generate():
                yield 1
                raise StopIteration(2)
            iterator = generate()
            first = next(iterator)
            try: next(iterator)
            except RuntimeError as error: failure = (error.__cause__.value, error.__context__.value)
            (first, failure, next(iterator, 3))
            """).Repr());
    }

    [Fact]
    public void ReentrantNextFailsWithoutCorruptingTheRunningFrame()
    {
        Assert.Equal("[1, 2]", new PythonEngine().Evaluate("""
            def generate():
                try: next(iterator)
                except ValueError: pass
                yield 1
                yield 2
            iterator = generate()
            list(iterator)
            """).Repr());
    }

    [Theory]
    [InlineData("def g():\n    try: yield 1\n    finally: pass\nnext(g())", "yield in this position")]
    [InlineData("def g():\n    print((yield 1))\nnext(g())", "yield in this position")]
    [InlineData("def g(): yield 1\ng().send(None)", "generator.send()")]
    public void UnsupportedSuspensionPositionsAndOperationsReportAnError(string source, string message)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source));
        Assert.Equal("NotImplementedError", error.ErrorName);
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void EnforcesExecutionAndRecursionBudgetsOnResume()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 120, MaximumCallDepth = 8 });
        engine.Evaluate("""
            def generate():
                while True: yield 1
            def recursive():
                yield from recursive()
            iterator = generate()
            """);
        var steps = Assert.Throws<PythonException>(() => engine.Evaluate("list(iterator)"));
        Assert.Contains("maximum execution steps", steps.Message);
        Assert.Equal("99", engine.Evaluate("next(iterator, 99)").Repr());
        var depth = Assert.Throws<PythonException>(() => engine.Evaluate("next(recursive())"));
        Assert.Equal("RecursionError", depth.ErrorName);
    }
}
