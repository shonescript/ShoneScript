// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy;

namespace ShonePy.Tests;

public sealed class PyCollectionsModuleTests
{
    [Theory]
    [InlineData("Counter()", "Counter()")]
    [InlineData("Counter(None)", "Counter()")]
    [InlineData("Counter('banana')", "Counter({'a': 3, 'n': 2, 'b': 1})")]
    [InlineData("Counter(iter(['🦊', 'x', '🦊']))", "Counter({'🦊': 2, 'x': 1})")]
    [InlineData("Counter({'a': -1, 'b': 0}, a=3, iterable=4)", "Counter({'iterable': 4, 'a': 2, 'b': 0})")]
    [InlineData("Counter([True, 1, 1.0])", "Counter({True: 3})")]
    [InlineData("Counter('cabba').most_common(n=2)", "[('a', 2), ('b', 2)]")]
    [InlineData("Counter('cabba').most_common(-1)", "[]")]
    [InlineData("Counter(a=1).most_common(9223372036854775808)", "[('a', 1)]")]
    [InlineData("Counter(z=0) == Counter()", "True")]
    [InlineData("Counter(z=0) == {}", "False")]
    [InlineData("{'x': 2} == Counter(x=2)", "True")]
    public void EvaluatesCounterExpressions(string expression, string expected)
    {
        var engine = new PythonEngine();
        Assert.Equal(expected, engine.Evaluate("from collections import Counter\n" + expression).Repr());
    }

    [Fact]
    public void MissingSubscriptDoesNotChangeMembershipOrOverrideGetDefault()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            import collections
            c = collections.Counter('ab')
            missing = c['z']
            before = (missing, 'z' in c, c.get('z'), c.get('z', 4), len(c))
            c['z'] = 0
            c['other'] += 1
            (before, 'z' in c, c.get('z', 4), list(c))
            """);
        Assert.Equal("((0, False, None, 4, 2), True, 0, ['a', 'b', 'z', 'other'])", result.Repr());
        Assert.Equal("KeyError", Assert.Throws<PythonException>(() => engine.Evaluate("{}['z']")).ErrorName);
    }

    [Fact]
    public void UpdateSubtractTotalAndCopyRetainSignedCounts()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            from collections import Counter
            c = Counter('aba')
            c.update('aca', b=3)
            c.update(c)
            d = c.copy()
            changed = c.subtract({'a': 9, 'b': 8}, d=2)
            c.subtract('ca')
            (c.most_common(), c.total(), list(c.elements()), d.most_common(), changed)
            """);
        Assert.Equal("([('c', 1), ('b', 0), ('a', -2), ('d', -2)], -3, ['c'], [('a', 8), ('b', 8), ('c', 2)], None)", result.Repr());
    }

    [Fact]
    public void ElementsIsAResumableIteratorWithLargeCounts()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            from collections import Counter
            from itertools import islice
            c = Counter(skip=-5, first=9223372036854775807, later=2)
            e = c.elements()
            (next(e), list(islice(e, 2)), iter(e) is e)
            """);
        Assert.Equal("('first', ['first', 'first'], True)", result.Repr());
    }

    [Fact]
    public void ElementsReportsRepeatCountOverflowWhenAdvanced()
    {
        var engine = new PythonEngine();
        engine.Evaluate("from collections import Counter\ne = Counter(a=9223372036854775808).elements()");
        Assert.Equal("OverflowError", Assert.Throws<PythonException>(() => engine.Evaluate("next(e)")).ErrorName);
    }

    [Theory]
    [InlineData("Counter(1)")]
    [InlineData("Counter('a', 'b')")]
    [InlineData("Counter([[]])")]
    [InlineData("Counter()[('missing', [])]")]
    [InlineData("list(Counter(a=1.5).elements())")]
    [InlineData("Counter(a=1, b='x').most_common()")]
    [InlineData("Counter().most_common(1, n=2)")]
    public void ReportsPythonTypeErrors(string expression)
    {
        var engine = new PythonEngine();
        Assert.Equal("TypeError", Assert.Throws<PythonException>(() =>
            engine.Evaluate("from collections import Counter\n" + expression)).ErrorName);
    }
}
