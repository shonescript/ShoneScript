// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonStarredExpressionTests
{
    [Fact]
    public void CollectsTheMiddleIntoAListAndBindsNestedTargets()
    {
        var value = new PythonEngine().Evaluate("""
            def unpack():
                a, *middle, (last, *empty) = iter([1, 2, 3, (4,)])
                return (a, middle, last, empty, [*middle, last])
            unpack()
            """);
        Assert.Equal("(1, [2, 3], 4, [], [2, 3, 4])", value.Repr());
    }

    [Fact]
    public void DoesNotAssignImmediateTargetsWhenTooFewValuesAreAvailable()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            a = 42
            try: a, *rest, b = [1]
            except ValueError: pass
            a
            """);
        Assert.Equal("42", result.Repr());
    }

    [Theory]
    [InlineData("*a = [1]")]
    [InlineData("a, *b, *c = [1]")]
    [InlineData("a = *b")]
    [InlineData("[*x, y for x in z]")]
    public void RejectsInvalidStarredSyntaxDuringParsing(string source)
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().ParseScript(source));
        Assert.Equal("SyntaxError", error.ErrorName);
    }
}
