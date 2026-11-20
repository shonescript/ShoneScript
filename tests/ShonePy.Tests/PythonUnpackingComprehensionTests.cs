// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonUnpackingComprehensionTests
{
    [Theory]
    [InlineData("x = 42\n([*x for x in [[], [1, 2], (), 'ab']], x)", "([1, 2, 'a', 'b'], 42)")]
    [InlineData("[*range(y) for x in [[], [2, 1], [3]] if x for y in x if y != 1]", "[0, 1, 0, 1, 2]")]
    [InlineData("sorted({*x for x in [[1, 2], [], (2, 3)]})", "[1, 2, 3]")]
    [InlineData("{**x or {3: 'c'} for x in [{1: 'a', 2: 'b'}, {}, {1: 'new'}]}", "{1: 'new', 2: 'b', 3: 'c'}")]
    [InlineData("{0: 'a', **{1: 'b'}, 1: 'c', **{2: 'd'}}", "{0: 'a', 1: 'c', 2: 'd'}")]
    [InlineData("([*missing for x in []], {*missing for x in []}, {**missing for x in []})", "([], set(), {})")]
    [InlineData("def pair(a, b): return list(a), b\n(list(*x or [0] for x in [[], [1, 2]]), pair((*x for x in [[3]]), 4))", "([0, 1, 2], ([3], 4))")]
    [InlineData("list((*x if x else [9] for x in [[], [1, 2]]))", "[9, 1, 2]")]
    public void EvaluatesUnpackingForms(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).Repr());

    [Theory]
    [InlineData("[*x for x in [1]]", "TypeError")]
    [InlineData("{**x for x in [[('a', 1)]]}", "TypeError")]
    [InlineData("[*x or [] for x in [[1]]]", "SyntaxError")]
    [InlineData("{*not x for x in [[1]]}", "SyntaxError")]
    [InlineData("{**x or {} }", "SyntaxError")]
    [InlineData("{**x: 1 for x in [{}]}", "SyntaxError")]
    [InlineData("[**x for x in [{}]]", "SyntaxError")]
    [InlineData("list(**x for x in [{}])", "SyntaxError")]
    [InlineData("list(*x for x in [[1]],)", "SyntaxError")]
    [InlineData("print(0, *x for x in [[1]])", "SyntaxError")]
    [InlineData("def f(): return [*(yield x) for x in [[1]]]", "SyntaxError")]
    public void ReportsUnpackingErrors(string source, string expected) =>
        Assert.Equal(expected, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Fact]
    public void PreservesTheInnerCursorBetweenGeneratorResumptions()
    {
        const string source = """
            events = []
            def mark(label, value):
                events.append(label)
                return value
            def items():
                yield mark('first item', 1)
                yield mark('second item', 2)
                yield mark('third item', 3)
            inner = items()
            g = (*mark('unpack', row) for row in mark('outer', [inner, [4]]) if mark('filter', row))
            before = tuple(events)
            first = next(g)
            middle = tuple(events)
            second = next(inner)
            (before, first, middle, second, list(g), events)
            """;
        Assert.Equal("(('outer',), 1, ('outer', 'filter', 'unpack', 'first item'), 2, [3, 4], ['outer', 'filter', 'unpack', 'first item', 'second item', 'third item', 'filter', 'unpack'])",
            new PythonEngine().Evaluate(source).Repr());
    }

    [Fact]
    public void ChargesTheStepBudgetWhileExpandingARow()
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 60 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("[*range(10**30) for x in [0]]"));
        Assert.Equal("maximum execution steps exceeded", error.Message);
    }
}
