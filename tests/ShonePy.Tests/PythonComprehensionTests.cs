// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonComprehensionTests
{
    [Theory]
    [InlineData("[x * x for x in range(5) if x % 2]", "[1, 9]")]
    [InlineData("[a + b for a, [b] in [(1, [2]), (3, [4])]]", "[3, 7]")]
    [InlineData("[x for x, in [(1,), (2,)]]", "[1, 2]")]
    [InlineData("x = [0]\n[x[0] for x[0] in [1, 2]], x", "([1, 2], [2])")]
    [InlineData("[x if x else 9 for x in ([0, 2] if True else missing) if (True if x < 3 else missing)]", "[9, 2]")]
    [InlineData("[[x for x in x] for x in [[1, 2], [3]]]", "[[1, 2], [3]]")]
    [InlineData("{(x, y): x+y for x in [1, 2] for y in range(x) if y == 0}", "{(1, 0): 1, (2, 0): 2}")]
    [InlineData("{x % 2: x for x in range(4)}", "{0: 2, 1: 3}")]
    [InlineData("[missing for x in [] for y in missing]", "[]")]
    [InlineData("{missing: missing for x in [1] if False if missing}", "{}")]
    [InlineData("x = [1, 2]\ndef f(): return [x for x in x], x\nf()", "([1, 2], [1, 2])")]
    [InlineData("def f(x): return [x for x in x], x\nf([1, 2])", "([1, 2], [1, 2])")]
    [InlineData("x = 1\ndef outer():\n    x = 2\n    def inner():\n        global x\n        return [x for i in [0]]\n    return inner()\nouter()", "[1]")]
    public void EvaluatesComprehensions(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).ToString());

    [Fact]
    public void EvaluatesIterablesFiltersAndDictEntriesInOrder()
    {
        const string source = """
            seen = []
            def mark(label, value):
                seen.append(label)
                return value
            result = {
                mark('key', (x, y)): mark('value', x + y)
                for x in mark('outer', [0, 2, 1])
                if mark(x, x)
                if mark('second filter', True)
                for y in mark('inner', range(x))
                if mark(y, y)
            }
            result, seen
            """;
        const string expected = "({(2, 1): 3}, ['outer', 0, 2, 'second filter', 'inner', 0, 1, 'key', 'value', 1, 'second filter', 'inner', 0])";
        Assert.Equal(expected, new PythonEngine().Evaluate(source).ToString());
    }

    [Theory]
    [InlineData("[x for x in [1]]\nx", "NameError")]
    [InlineData("y = [1]\n[y for x in [0] for y in y]", "UnboundLocalError")]
    [InlineData("y = 1\n[y for x in [0] if y for y in [1]]", "UnboundLocalError")]
    [InlineData("[x for x in 1]", "TypeError")]
    [InlineData("{[x]: x for x in [1]}", "TypeError")]
    [InlineData("[x for x + 1 in [1]]", "SyntaxError")]
    [InlineData("[x for x in [1],]", "SyntaxError")]
    [InlineData("[x, x for x in [1]]", "SyntaxError")]
    [InlineData("[x for x in [1] if True else False]", "SyntaxError")]
    [InlineData("{x: x for x [1]}", "SyntaxError")]
    public void ReportsComprehensionErrors(string source, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Fact]
    public void ChargesTheStepBudgetEvenWhenInnerLoopsProduceNothing()
    {
        // Each range pull also charges a step. Let the first outer iteration visit the empty inner clause.
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 11 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("""
            x = 42
            [
                x
                for x in range(100)
                for y in []
            ]
            """));
        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal(4, error.Line);
        Assert.Equal("42", engine.Evaluate("x").ToString());
    }
}
