// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy;

namespace ShonePy.Tests;

public sealed class PythonSetTests
{
    [Theory]
    [InlineData("(repr(set()), repr({}), bool(set()), bool({0}), len({1, 2, 1,}))", "('set()', '{}', False, True, 2)")]
    [InlineData("(set('abba') == {'a', 'b'}, set({1: 2}) == {1}, set(iter([1])) == {1})", "(True, True, True)")]
    [InlineData("{*range(3), 2, *[4, 0]} == {0, 1, 2, 4}", "True")]
    [InlineData("({1, True, 1.0}, (1.0, 'x') in {(True, 'x')}, 3 not in {1, 2})", "({1}, True, True)")]
    [InlineData("(len({range(0, 8, 3), range(0, 9, 3)}), set() in {1})", "(1, False)")]
    [InlineData("({x + y for x in range(4) if x > 0 for y in range(x) if y < 2}, {x: x for x in [1]}) == ({1, 2, 3, 4}, {1: 1})", "True")]
    [InlineData("(sorted({1, 2} | {2, 3}), sorted({1, 2} & {2, 3}), sorted({1, 2} - {2, 3}), sorted({1, 2} ^ {2, 3}))", "([1, 2, 3], [2], [1], [1, 3])")]
    [InlineData("({1} < {1, 2}, {1} <= {True}, {2} < {1}, {1, 2} > {1}, {1} >= {1}, {1} != [1])", "(True, True, False, True, True, True)")]
    [InlineData("(sorted({1}.union([2], (3,))), sorted({1, 2, 3}.intersection([2, 3], (1, 3))), sorted({1, 2, 3}.difference([1], (3,))), sorted({1, 2}.symmetric_difference([2, 3, 3])))", "([1, 2, 3], [3], [2], [1, 3])")]
    [InlineData("({1, 2}.issubset(range(3)), {1, 2}.issuperset([1, 1]), {1, 2}.isdisjoint('12'), {1}.isdisjoint(range(3)))", "(True, True, True, False)")]
    public void SetExpressionsFollowPythonSemantics(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).Repr());

    [Fact]
    public void SetOperationsPreserveTheOriginalUnlessInPlace()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyType.Set, engine.Evaluate("{1}").Type);
        var result = engine.Evaluate("""
            source = {1, 2}
            alias = source
            copy = source.copy()
            result = source | {3}
            source |= {4}
            source &= {2, 4}
            source ^= {4, 5}
            source -= {5}
            (source is alias, source == {2}, copy == {1, 2}, copy is source, result == {1, 2, 3})
            """);
        Assert.Equal("(True, True, True, False, True)", result.Repr());
    }

    [Fact]
    public void SetsSupportMutationMethods()
    {
        var result = new PythonEngine().Evaluate("""
            s = set()
            a = s.add(1)
            b = s.update([2, 3], [3, 4])
            c = s.intersection_update(range(1, 4))
            d = s.symmetric_difference_update([1, 4, 4])
            e = s.difference_update([2], [4])
            f = s.discard(set())
            g = s.add(5)
            h = s.remove(3)
            item = s.pop()
            s.update({1, 2})
            i = s.clear()
            (a, b, c, d, e, f, g, h, item, i, len(s))
            """);
        Assert.Equal("(None, None, None, None, None, None, None, None, 5, None, 0)", result.Repr());
    }

    [Fact]
    public void IteratorsTrackSetMutationAndComprehensionTargetsStayLocal()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            x = 9
            s = {x for x in [1, 1]}
            cursor = iter(s)
            s.add(1)
            (x, next(cursor), next(cursor, 0))
            """);
        Assert.Equal("(9, 1, 0)", result.Repr());
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("cursor = iter(s)\ns.add(2)\nnext(cursor)"));
        Assert.Equal("RuntimeError", error.ErrorName);
    }

    [Theory]
    [InlineData("{[]}", "TypeError")]
    [InlineData("{x for x in [[]]}", "TypeError")]
    [InlineData("{(set(),)}", "TypeError")]
    [InlineData("{set(): 'key'}", "TypeError")]
    [InlineData("hash(set())", "TypeError")]
    [InlineData("[] in set()", "TypeError")]
    [InlineData("{1} | [2]", "TypeError")]
    [InlineData("{1} <= [1]", "TypeError")]
    [InlineData("set(iterable=[1])", "TypeError")]
    [InlineData("set(1, 2)", "TypeError")]
    [InlineData("set().add()", "TypeError")]
    [InlineData("set().pop()", "KeyError")]
    [InlineData("set().remove(1)", "KeyError")]
    [InlineData("{*x for x in [[[1]]]}", "TypeError")]
    public void InvalidSetOperationsReportPythonErrors(string source, string expected) =>
        Assert.Equal(expected, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Theory]
    [InlineData("set(range(10**30))")]
    [InlineData("set().update(range(10**30))")]
    [InlineData("{x for x in range(10**30)}")]
    public void ConsumingIterablesChargesTheExecutionBudget(string source)
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 60 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate(source));
        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Equal("maximum execution steps exceeded", error.Message);
    }
}
