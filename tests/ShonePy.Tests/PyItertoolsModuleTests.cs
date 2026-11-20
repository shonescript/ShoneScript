// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy;

namespace ShonePy.Tests;

public sealed class PyItertoolsModuleTests
{
    [Theory]
    [InlineData("list(chain([1, 2], (), [3]))", "[1, 2, 3]")]
    [InlineData("list(islice(count(2), 1, 8, 3))", "[3, 6, 9]")]
    [InlineData("list(islice(repeat(None), None, 2, None))", "[None, None]")]
    [InlineData("list(repeat(object='x', times=2))", "['x', 'x']")]
    [InlineData("list(repeat(0, -3))", "[]")]
    [InlineData("list(accumulate([1, 2, 3]))", "[1, 3, 6]")]
    [InlineData("list(accumulate(iterable=[], initial=4))", "[4]")]
    [InlineData("list(accumulate([None], initial=None))", "[None]")]
    [InlineData("tuple(islice(count(step=-2), 3))", "(0, -2, -4)")]
    [InlineData("list(islice(count(1, 1.5), 3))", "[1, 2.5, 4.0]")]
    [InlineData("list(islice(count(9223372036854775807), 2))", "[9223372036854775807, 9223372036854775808]")]
    public void ProducesExpectedValues(string expression, string expected)
    {
        var engine = new PythonEngine();
        Assert.Equal(expected, engine.Evaluate("from itertools import chain, islice, repeat, accumulate, count\n" + expression).Repr());
    }

    [Fact]
    public void IteratorIsSinglePassAndCanResumeAfterBreak()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            import itertools
            cursor = itertools.chain([1, 2, 3], [4])
            for first in cursor:
                break
            (first, iter(cursor) is cursor, next(cursor), list(cursor), next(cursor, 10), list(cursor))
            """);
        Assert.Equal("(1, True, 2, [3, 4], 10, [])", result.Repr());
    }

    [Fact]
    public void IsliceAdvancesSharedInputToTheLargerBoundWhenExhausted()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            from itertools import islice
            source = iter([0, 1, 2, 3, 4, 5, 6, 7])
            first = list(islice(source, 1, 4, 9))
            second = list(islice(source, 2, 1))
            (first, second, next(source))
            """);
        Assert.Equal("([1], [], 6)", result.Repr());
    }

    [Fact]
    public void AccumulateDefersCallbackAndPreservesInitialValue()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            from itertools import accumulate
            calls = []
            def multiply(a, b):
                calls.append(b)
                return a * b
            source = [2]
            totals = accumulate(source, func=multiply, initial=10)
            initial = next(totals)
            before = len(calls)
            source.append(3)
            (initial, before, list(totals), calls)
            """);
        Assert.Equal("(10, 0, [20, 60], [2, 3])", result.Repr());
    }

    [Fact]
    public void ChainOpensEachInputOnlyWhenNeeded()
    {
        var engine = new PythonEngine();
        engine.Evaluate("from itertools import chain\ncursor = chain([1], 2, [3])");
        Assert.Equal(PyValue.From(1), engine.Evaluate("next(cursor)"));
        Assert.Equal("TypeError", Assert.Throws<PythonException>(() => engine.Evaluate("next(cursor)")).ErrorName);
        Assert.Equal("StopIteration", Assert.Throws<PythonException>(() => engine.Evaluate("next(cursor)")).ErrorName);
    }

    [Theory]
    [InlineData("islice([], -1)", "ValueError")]
    [InlineData("islice([], 1, 2, 0)", "ValueError")]
    [InlineData("islice([], 9223372036854775808)", "ValueError")]
    [InlineData("islice([], 1.5)", "ValueError")]
    [InlineData("islice(7, 2)", "TypeError")]
    [InlineData("count('x')", "TypeError")]
    [InlineData("repeat(1, None)", "TypeError")]
    [InlineData("repeat(1, 9223372036854775808)", "OverflowError")]
    [InlineData("accumulate([1], None, 4)", "TypeError")]
    public void ReportsPythonArgumentErrors(string expression, string expected)
    {
        var engine = new PythonEngine();
        var exception = Assert.Throws<PythonException>(() => engine.Evaluate("from itertools import islice, count, repeat, accumulate\n" + expression));
        Assert.Equal(expected, exception.ErrorName);
    }

    [Theory]
    [InlineData("list(count())")]
    [InlineData("sum(repeat(1))")]
    [InlineData("list(islice(count(), 1000000000, 1000000001))")]
    [InlineData("list(chain(*repeat(())))")]
    [InlineData("list(chain(*repeat((), 100)))")]
    public void ChargesNativeIteratorWorkToExecutionBudget(string expression)
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 180 });
        var exception = Assert.Throws<PythonException>(() => engine.Evaluate("from itertools import chain, count, islice, repeat\n" + expression));
        Assert.Equal("RuntimeError", exception.ErrorName);
        Assert.Contains("maximum execution steps exceeded", exception.Message);
        Assert.Equal("[0, 1, 2]", engine.Evaluate("list(islice(count(), 3))").Repr());
    }

    [Fact]
    public void LimitsNativeIteratorNesting()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumCallDepth = 6 });
        var exception = Assert.Throws<PythonException>(() => engine.Evaluate("""
            from itertools import chain, repeat
            cursor = repeat(0)
            for i in range(10):
                cursor = chain(cursor)
            next(cursor)
            """));
        Assert.Equal("RecursionError", exception.ErrorName);
    }
}
