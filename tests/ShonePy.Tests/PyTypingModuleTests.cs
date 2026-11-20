// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PyTypingModuleTests
{
    [Fact]
    public void CastAndAssertTypePreserveIdentityWithoutCheckingTheType()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyValue.True, engine.Evaluate("""
            import typing
            from typing import cast as cast_value, assert_type
            value = [1, 2]
            (typing.cast('str', value) is value
                and cast_value('str', val=value) is value
                and cast_value(val=value, typ=None) is value
                and assert_type(value, 'str') is value)
            """));
    }

    [Theory]
    [InlineData("cast('str')")]
    [InlineData("cast('str', 1, 2)")]
    [InlineData("cast('str', typ='int', val=1)")]
    [InlineData("cast('str', 1, val=2)")]
    [InlineData("cast(typ='str', value=1)")]
    [InlineData("assert_type(1)")]
    [InlineData("assert_type(1, typ='int')")]
    [InlineData("assert_never()")]
    [InlineData("assert_never(arg=1)")]
    public void HelpersRejectInvalidArguments(string call)
    {
        var engine = new PythonEngine();
        var error = Assert.Throws<PythonException>(() => engine.Evaluate($"from typing import cast, assert_type, assert_never\n{call}"));
        Assert.Equal("TypeError", error.ErrorName);
    }

    [Fact]
    public void AssertNeverRaisesACatchableAssertionErrorWithTheValueRepr()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyValue.From("Expected code to be unreachable, but got: ['unexpected']"), engine.Evaluate("""
            from typing import assert_never
            message = None
            try:
                assert_never(['unexpected'])
            except AssertionError as error:
                message = str(error)
            message
            """));
    }

    [Fact]
    public void AssertNeverTruncatesLongReprsByUnicodeCodePoint()
    {
        var engine = new PythonEngine();
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("from typing import assert_never\nassert_never('😀' * 100)"));
        Assert.Equal("AssertionError", error.ErrorName);
        Assert.Equal("Expected code to be unreachable, but got: '" + string.Concat(Enumerable.Repeat("😀", 99)) + "...", error.Message);
    }

    [Fact]
    public void TypingAndDatetimeCanBeImportedTogether()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyValue.From("2024-02-29"), engine.Evaluate("""
            from typing import cast
            import datetime
            cast('date', datetime.date(2024, 2, 29)).isoformat()
            """));
    }
}
