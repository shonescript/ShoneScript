// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonGeneratorExpressionTests
{
    [Fact]
    public void EvaluatesOnlyTheOuterIterableAtConstruction()
    {
        const string source = """
            events = []
            def mark(label, value):
                events.append(label)
                return value
            def source():
                yield mark('first pull', 2)
                yield mark('second pull', 3)
            g = (mark('element', x + y) for x in mark('outer', source())
                 if mark('filter', x) for y in mark('inner', range(x)))
            before = tuple(events)
            first = next(g)
            after = tuple(events)
            second = next(g)
            (before, first, after, second, events)
            """;
        Assert.Equal("(('outer',), 2, ('outer', 'first pull', 'filter', 'inner', 'element'), 3, ['outer', 'first pull', 'filter', 'inner', 'element', 'element'])",
            new PythonEngine().Evaluate(source).ToString());
    }

    [Theory]
    [InlineData("list(x * x for x in range(4) if x % 2)", "[1, 9]")]
    [InlineData("def pair(a, b): return list(a), b\npair((x for x in [1, 2]), 3)", "([1, 2], 3)")]
    [InlineData("g = (missing for x in [] for y in missing)\nlist(g)", "[]")]
    [InlineData("x = [2, 3]\ng = (x + bias for x in x)\nbias = 10\nfirst = next(g)\nbias = 20\n(first, list(g), x)", "(12, [23], [2, 3])")]
    [InlineData("def make(xs):\n    g = (x + bias for x in xs)\n    bias = 10\n    return g\nlist(make([1, 2]))", "[11, 12]")]
    [InlineData("xs = [1]\ng = (x for x in xs)\nxs.append(2)\nlist(g)", "[1, 2]")]
    [InlineData("source = iter([1, 2, 3, 4])\ng = (x for x in source)\nfirst = next(source)\nfor item in g: break\n(iter(g) is g, first, item, list(g), next(g, 0))", "(True, 1, 2, [3, 4], 0)")]
    public void EvaluatesSupportedForms(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).ToString());

    [Theory]
    [InlineData("(x for x in 1)", "TypeError")]
    [InlineData("g = (x for x in [1] for y in 2)\nnext(g)", "TypeError")]
    [InlineData("(x for x in [1])\nx", "NameError")]
    [InlineData("g = (y for x in [1] if y for y in [2])\nnext(g)", "UnboundLocalError")]
    [InlineData("list(x for x in [1],)", "SyntaxError")]
    [InlineData("print(1, x for x in [1])", "SyntaxError")]
    [InlineData("print(x for x in [1], end='')", "SyntaxError")]
    [InlineData("g = (*x for x in [1])\nnext(g)", "TypeError")]
    [InlineData("def f(): return ((yield x) for x in [1])", "SyntaxError")]
    public void ReportsErrors(string source, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Fact]
    public void ExhaustsAfterAnEscapingStopIterationAndRestoresCallerScope()
    {
        const string source = """
            x = 42
            def fail(): raise StopIteration('inner')
            g = (fail() for x in [1, 2])
            try: next(g)
            except RuntimeError as error: result = error.__cause__.args
            (result, next(g, -1), x)
            """;
        Assert.Equal("(('inner',), -1, 42)", new PythonEngine().Evaluate(source).ToString());
    }
}
