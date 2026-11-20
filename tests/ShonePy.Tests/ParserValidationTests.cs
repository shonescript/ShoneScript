// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Parsing;

namespace ShonePy.Tests;

public sealed class ParserValidationTests
{
    [Theory]
    [InlineData("return")]
    [InlineData("break")]
    [InlineData("continue")]
    [InlineData("if True:\n    break")]
    [InlineData("while True:\n    def f():\n        continue")]
    public void RejectsControlFlowOutsideValidContext(string source) =>
        Assert.Throws<PythonException>(() => Parser.Parse(source));

    [Theory]
    [InlineData("1 = value")]
    [InlineData("(a + b) = value")]
    [InlineData("(a, 1) = value")]
    [InlineData("[a] += value")]
    [InlineData("for 1 in values:\n    pass")]
    public void RejectsInvalidAssignmentTargets(string source) =>
        Assert.Throws<PythonException>(() => Parser.Parse(source));

    [Fact]
    public void RejectsDuplicateParameters() =>
        Assert.Throws<PythonException>(() => Parser.Parse("def f(value, value):\n    pass"));

    [Theory]
    [InlineData("def f(*):\n    pass")]
    [InlineData("def f(*, value):\n    global value\n    pass")]
    [InlineData("def f(**value):\n    nonlocal value\n    pass")]
    public void VariadicAndKeywordOnlyParametersParticipateInScopeChecks(string source) =>
        Assert.Throws<PythonException>(() => Parser.Parse(source));

    [Fact]
    public void RetainsRuntimeFilenameOnModule() =>
        Assert.Equal("runtime.py", Parser.Parse("pass", "runtime.py").FileName);

    [Fact]
    public void AllowsLoopControlInsideLoopsAndReturnsInsideFunctions()
    {
        var syntax = Parser.Parse("while True:\n    break\ndef f():\n    return 1");
        Assert.Equal(2, syntax.Statements.Count);
    }
}
