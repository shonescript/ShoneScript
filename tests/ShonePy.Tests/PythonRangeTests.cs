// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy;

namespace ShonePy.Tests;

public sealed class PythonRangeTests
{
    [Theory]
    [InlineData("range(4)", "range(0, 4)")]
    [InlineData("range(True, 5, True)", "range(1, 5)")]
    [InlineData("(range(9, 0, -3).start, range(9, 0, -3).stop, range(9, 0, -3).step)", "(9, 0, -3)")]
    [InlineData("(bool(range(5, 0)), bool(range(10**40)), len(range(5, 0)))", "(False, True, 0)")]
    [InlineData("len(range(10**40, 10**40 + 13, 3))", "5")]
    [InlineData("list(range(2147483646, 2147483651, 2))", "[2147483646, 2147483648, 2147483650]")]
    [InlineData("tuple(range(10, -2, -4))", "(10, 6, 2)")]
    [InlineData("(range(10**30)[-1], range(10**30)[10**29], range(10**30)[-10**30])", "(999999999999999999999999999999, 100000000000000000000000000000, 0)")]
    [InlineData("range(5, 20, 3)[True]", "8")]
    [InlineData("range(10)[8:1:-2]", "range(8, 1, -2)")]
    [InlineData("range(10)[8:-1:-2]", "range(8, 9, -2)")]
    [InlineData("range(10)[8::-2]", "range(8, -1, -2)")]
    [InlineData("range(3, 14, 4)[:]", "range(3, 15, 4)")]
    [InlineData("range(-2, -12, -3)[::-1]", "range(-11, 1, 3)")]
    [InlineData("range(0)[::-1]", "range(-1, -1, -1)")]
    [InlineData("range(5, -5, -2)[10**50:-10**50:-2]", "range(-3, 7, 4)")]
    [InlineData("range(0, 1, 2)[::-10**30]", "range(0, -2, -2000000000000000000000000000000)")]
    [InlineData("range(10)[None:None:True]", "range(0, 10)")]
    [InlineData("list(range(10**40)[::-10**39][:3])", "[9999999999999999999999999999999999999999, 8999999999999999999999999999999999999999, 7999999999999999999999999999999999999999]")]
    [InlineData("(10**38 in range(0, 10**40, 2), 10**38 + 1 in range(0, 10**40, 2), 10**40 in range(10**40))", "(True, False, False)")]
    [InlineData("(-1 in range(5, -10, -3), -10 in range(5, -10, -3), -2 in range(5, -10, -3))", "(True, False, False)")]
    [InlineData("(True in range(2), 2.0 in range(4), 0.5 in range(4), '2' in range(4), float('nan') in range(4))", "(True, True, False, False, False)")]
    [InlineData("9007199254740992.0 in range(9007199254740993, 9007199254740994)", "False")]
    [InlineData("(range(10**30).index(10**30-1), range(5, -9, -2).index(-3.0), range(4).count(0.0), range(4).count('0'))", "(999999999999999999999999999999, 4, 1, 0)")]
    [InlineData("(range(0) == range(3, 2), range(3) == [0, 1, 2], range(3) == (0, 1, 2), range(3) is range(3))", "(True, False, False, False)")]
    [InlineData("(range(1, 2, 7) == range(1, 0, -3), range(0, 9, 3) == range(0, 8, 3), range(0, 9, 3) == range(0, 9, 4))", "(True, True, False)")]
    [InlineData("(hash(range(0)) == hash(range(3, 2)), hash(range(1, 2, 7)) == hash(range(1, 0, -3)), hash(range(0, 9, 3)) == hash(range(0, 8, 3)))", "(True, True, True)")]
    [InlineData("{range(0, 9, 3): 'match'}[range(0, 8, 3)]", "'match'")]
    public void MatchesPythonRangeSemantics(string expression, string expected) =>
        Assert.Equal(expected, new PythonEngine(new PythonOptions { MaximumSteps = 200 }).Evaluate(expression).Repr());

    [Theory]
    [InlineData("range()", "TypeError")]
    [InlineData("range(1, 2, 3, 4)", "TypeError")]
    [InlineData("range(stop=3)", "TypeError")]
    [InlineData("range(1.0)", "TypeError")]
    [InlineData("range('2', 3)", "TypeError")]
    [InlineData("range(1, 2, [])", "TypeError")]
    [InlineData("range(0, 3, False)", "ValueError")]
    [InlineData("range(4)[1.0]", "TypeError")]
    [InlineData("range(4)[10**40]", "IndexError")]
    [InlineData("range(4)[-10**40]", "IndexError")]
    [InlineData("range(4)[::0]", "ValueError")]
    [InlineData("range(4)[1.0:]", "TypeError")]
    [InlineData("range(4)[0] = 1", "TypeError")]
    [InlineData("range(4)[:] = [1]", "TypeError")]
    [InlineData("range(4).append(0)", "AttributeError")]
    [InlineData("range(4).index(0.2)", "ValueError")]
    [InlineData("range(4).count()", "TypeError")]
    [InlineData("range(4) < range(5)", "TypeError")]
    [InlineData("range(4) + range(5)", "TypeError")]
    [InlineData("next(range(4))", "TypeError")]
    [InlineData("len(range(10**40))", "OverflowError")]
    public void ReportsPythonErrors(string source, string expected) =>
        Assert.Equal(expected, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Fact]
    public void RangeHasItsOwnTypeAndIndependentIterators()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyType.Range, engine.Evaluate("range(1)").Type);
        var result = engine.Evaluate("""
            source = range(3)
            cursor = iter(source)
            for first in cursor:
                break
            (first, iter(cursor) is cursor, list(source), next(cursor), list(cursor), next(cursor, 99))
            """);
        Assert.Equal("(0, True, [0, 1, 2], 1, [2], 99)", result.Repr());

        var items = PyOperations.Iterate(engine.GetGlobal("source"));
        Assert.Equal(items.ToArray(), items.ToArray());
    }

    [Theory]
    [InlineData("list(range(10**40))")]
    [InlineData("sum(range(10**40))")]
    [InlineData("sorted(range(10**40))")]
    [InlineData("[x for x in range(10**40) if False]")]
    public void ConsumingLargeRangesChargesTheExecutionBudget(string expression)
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 60 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate(expression));
        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("[0, 1, 2]", engine.Evaluate("list(range(10**40)[:3])").Repr());
    }
}
