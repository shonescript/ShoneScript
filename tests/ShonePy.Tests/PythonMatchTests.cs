// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonMatchTests
{
    [Fact]
    public void SoftKeywordsRemainIdentifiersAndTheSubjectRunsOnce()
    {
        AssertResult("([42, 43], 42, 42, 43)", """
            calls = []
            def match(case):
                calls.append(case)
                return case
            case = 42
            def classify():
                match match(43):
                    case case: return case
            prior = match(case)
            selected = classify()
            (calls, prior, case, selected)
            """);
    }

    [Fact]
    public void LiteralIdentityAndEqualityFollowPythonRules()
    {
        AssertResult("[3, 3, 1, 2, 4, 5, 6, 0]", """
            def classify(value):
                match value:
                    case True: return 1
                    case False: return 2
                    case 1 | 0: return 3
                    case None: return 4
                    case -2.5: return 5
                    case 'pi' 'zza' | b'pi' b'zza': return 6
                    case _: return 0
            [classify(x) for x in [1.0, 0, True, False, None, -2.5, b'pizza', 'other']]
            """);
    }

    [Fact]
    public void MatchesNestedSequencesAndOnlyPythonSequenceTypes()
    {
        AssertResult("[(1, [2, 3], 4), (1, [], 4), (0, [1, 2, 3], 4), 5, None, None, None, None]", """
            def classify(value):
                match value:
                    case head, *middle, (4): return (head, middle, 4)
                    case [(5,)]: return 5
            [classify(x) for x in [[1, 2, 3, 4], (1, 4), range(5), [(5,)], 'abc', b'abc', iter([1, 4]), {1: 4}]]
            """);
    }

    [Fact]
    public void CapturesSurviveFalseGuardsAndAreLocalEvenWhenThePatternFails()
    {
        AssertResult("((2, 9), 'unbound', 8)", """
            item = 8
            def choose(pair):
                match pair:
                    case [value, 1] if value == 0: return None
                    case [_, _]: return value, 9
            def local():
                match 0:
                    case [item]: pass
                try: return item
                except UnboundLocalError: return 'unbound'
            (choose([2, 1]), local(), item)
            """);
    }

    [Fact]
    public void SupportsGuardBindingsOrAsAndCaseBodyScopes()
    {
        AssertResult("((8, [1, 8], 8), (5, [5, 0], 5), 13)", """
            total = 0
            def classify(subject):
                global total
                match subject:
                    case ([value, 0] | [1, value]) as whole if value < 0: return None
                    case [_, _]:
                        def read():
                            nonlocal value
                            return value
                        total += read()
                        return (value, whole, read())
            first = classify([1, 8])
            second = classify([5, 0])
            (first, second, total)
            """);
    }

    [Fact]
    public void MatchesMappingKeysAndCopiesTheRemainder()
    {
        AssertResult("((3, {'more': 4}), 'empty', None, None)", """
            class Keys:
                first = 'value'
            def classify(subject):
                match subject:
                    case {Keys.first: [3 as value], **rest}: return (value, rest)
                    case {} if not subject: return 'empty'
            (classify({'value': [3], 'more': 4}), classify({}), classify({'other': [3]}), classify([]))
            """);
    }

    [Fact]
    public void RaisesForDuplicateDynamicMappingKeys()
    {
        AssertResult("'duplicate'", """
            class Keys:
                first = 'value'
                second = 'value'
            try:
                match {'value': 1, 'other': 2}:
                    case {Keys.first: _, Keys.second: _}: pass
            except ValueError:
                result = 'duplicate'
            result
            """);
    }

    [Fact]
    public void MatchesUserClassesWithInheritanceMatchArgsAndKeywordAttributes()
    {
        AssertResult("((2, 3), (4, 5), None, None)", """
            class Point:
                __match_args__ = ('x', 'y')
                def __init__(self, x, y): self.x, self.y = x, y
            class Colored(Point):
                def __init__(self, x, y, color):
                    super_init = Point.__init__
                    super_init(self, x, y)
                    self.color = color
            def classify(value):
                match value:
                    case Point(x, y): return x, y
                    case _: return None
            (classify(Colored(2, 3, 'red')), classify(Point(4, 5)), classify((2, 3)), classify(Point(1, 2)) if False else None)
            """);
        AssertResult("((8, 'blue'), 'blue', None)", """
            class Base: pass
            class Item(Base):
                def __init__(self): self.count, self.label = 8, 'blue'
            value = Item()
            match value:
                case Base(count=n, label='blue'): result = n, value.label
            (result, value.label, None)
            """);
    }

    [Fact]
    public void ClassPatternsRaiseForMalformedArgumentsAndMatchArgs()
    {
        AssertResult("['type', 'arity', 'tuple', 'elements', 'duplicate']", """
            class Item:
                __match_args__ = ('x',)
                def __init__(self): self.x = 1
            value = Item()
            NotAClass = 1
            results = []
            try:
                match value:
                    case NotAClass(): pass
            except TypeError: results.append('type')
            try:
                match value:
                    case Item(_, _): pass
            except TypeError: results.append('arity')
            Item.__match_args__ = 1
            try:
                match value:
                    case Item(_): pass
            except TypeError: results.append('tuple')
            Item.__match_args__ = (1,)
            try:
                match value:
                    case Item(_): pass
            except TypeError: results.append('elements')
            Item.__match_args__ = ('x', 'x')
            try:
                match value:
                    case Item(_, _): pass
            except TypeError: results.append('duplicate')
            results
            """);
    }

    [Theory]
    [InlineData("match 1: case _: pass")]
    [InlineData("match 1:\n    pass")]
    [InlineData("case 1: pass")]
    [InlineData("match 1:\n    case value: pass\n    case _: pass")]
    [InlineData("match 1:\n    case _ | 1: pass")]
    [InlineData("match 1:\n    case [a] | [b]: pass")]
    [InlineData("match 1:\n    case [a, a]: pass")]
    [InlineData("match 1:\n    case [*a, *b]: pass")]
    [InlineData("match 1:\n    case *a: pass")]
    [InlineData("match 1:\n    case 1 as _: pass")]
    [InlineData("match 1:\n    case {1: _, True: _}: pass")]
    [InlineData("match 1:\n    case {**_}: pass")]
    [InlineData("match 1:\n    case {'a': x, **x}: pass")]
    public void RejectsInvalidPatternsDuringParsing(string source)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().ParseScript(source));
        Assert.Equal("SyntaxError", error.ErrorName);
    }

    private static void AssertResult(string expected, string source) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).Repr());
}
