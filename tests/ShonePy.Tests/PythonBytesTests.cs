// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonBytesTests
{
    [Theory]
    [InlineData("(bytes(), bytes(3), bytes(True), bytes([0, True, 255]))", "(b'', b'\\x00\\x00\\x00', b'\\x00', b'\\x00\\x01\\xff')")]
    [InlineData("(bytes(range(4)), bytes(iter((0, 255))), bytes({65: 1, 66: 2}))", "(b'\\x00\\x01\\x02\\x03', b'\\x00\\xff', b'AB')")]
    [InlineData("bytes(source='café', encoding='UTF_8')", "b'caf\\xc3\\xa9'")]
    [InlineData("bytes('café', 'latin-1')", "b'caf\\xe9'")]
    [InlineData("(bytes('a😀b', 'ascii', 'replace'), bytes('aéb', 'ascii', errors='ignore'))", "(b'a?b', b'ab')")]
    [InlineData("(b'\\x00\\377\\n\\r\\t\\a\\b\\f\\v', B\"text\", b'\\u1234\\q')", "(b'\\x00\\xff\\n\\r\\t\\x07\\x08\\x0c\\x0b', b'text', b'\\\\u1234\\\\q')")]
    [InlineData("(br'\\n', Rb\"\\\"\", b'''a\r\nb''', b'a\\\r\nb')", "(b'\\\\n', b'\\\\\"', b'a\\nb', b'ab')")]
    [InlineData("(b'a' B'\\x62'\n br'c')", "b'abc'")]
    [InlineData("(b\"it's\", b'\"', b'\\\'\"')", "(b\"it's\", b'\"', b'\\'\"')")]
    [InlineData("(len(b'ab'), bool(b''), bool(b'a'), b'ab'[-1], b'ab'[False])", "(2, False, True, 98, 97)")]
    [InlineData("(b'abcde'[::-2], b'abcde'[1:4], b'abcde'[2:2], b'abc'[-10**40:10**40:10**40])", "(b'eca', b'bcd', b'', b'a')")]
    [InlineData("(b'ab' + b'cd', b'ab' * 3, 2 * b'ab', b'ab' * -1, b'ab' * True)", "(b'abcd', b'ababab', b'abab', b'', b'ab')")]
    [InlineData("(b'bc' in b'abcd', b'bd' not in b'abcd', b'' in b'', 97 in b'abc', True in b'\\x01')", "(True, True, True, True, True)")]
    [InlineData("(b'ab' == bytes([97, 98]), b'ab' != 'ab', b'ab' != [97, 98], b'\\xff' > b'\\x7f', b'a' < b'ab')", "(True, True, True, True, True)")]
    [InlineData("(hash(b'ab') == hash(bytes([97, 98])), {b'ab': 4}[bytes([97, 98])], len({b'ab', bytes([97, 98])}))", "(True, 4, 1)")]
    [InlineData("(list(b'AZ'), tuple(b'\\x00\\xff'), sum(b'ab'), str(b'abc'))", "([65, 90], (0, 255), 195, 'b\\'abc\\'')")]
    public void MatchesPythonBytesSemantics(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).Repr());

    [Theory]
    [InlineData("b'é'", "SyntaxError")]
    [InlineData("br'é'", "SyntaxError")]
    [InlineData("b'\\x1'", "SyntaxError")]
    [InlineData("b'abc", "SyntaxError")]
    [InlineData("(b'a' 'b')", "SyntaxError")]
    [InlineData("('a' b'b')", "SyntaxError")]
    [InlineData("bytes('abc')", "TypeError")]
    [InlineData("bytes(b'abc', 'ascii')", "TypeError")]
    [InlineData("bytes('abc', None)", "TypeError")]
    [InlineData("bytes(b'abc', source=b'ab')", "TypeError")]
    [InlineData("bytes(unknown=0)", "TypeError")]
    [InlineData("bytes(None)", "TypeError")]
    [InlineData("bytes([1.0])", "TypeError")]
    [InlineData("bytes([256])", "ValueError")]
    [InlineData("bytes([-1])", "ValueError")]
    [InlineData("bytes(-1)", "ValueError")]
    [InlineData("bytes(10**40)", "OverflowError")]
    [InlineData("bytes(8000001)", "OverflowError")]
    [InlineData("b'a' * 8000001", "OverflowError")]
    [InlineData("bytes(8000000) + b'a'", "OverflowError")]
    [InlineData("bytes('é', 'unknown')", "LookupError")]
    [InlineData("bytes('é', 'ascii', 'unknown')", "LookupError")]
    [InlineData("bytes('é', 'ascii')", "UnicodeEncodeError")]
    [InlineData("b'ab'[10**40]", "IndexError")]
    [InlineData("b'ab'[1.0]", "TypeError")]
    [InlineData("b'ab'[::0]", "ValueError")]
    [InlineData("'a' in b'a'", "TypeError")]
    [InlineData("256 in b'a'", "ValueError")]
    [InlineData("b'a' + 'b'", "TypeError")]
    [InlineData("b'a' < 'a'", "TypeError")]
    [InlineData("x = b'a'\nx[0] = 5", "TypeError")]
    [InlineData("x = b'a'\nx[:] = b'c'", "TypeError")]
    public void ReportsPythonErrors(string source, string expected) =>
        Assert.Equal(expected, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Fact]
    public void HasItsOwnTypeAndIndependentIterators()
    {
        var engine = new PythonEngine();
        var result = engine.Evaluate("""
            data = b'abc'
            cursor = iter(data)
            for first in cursor:
                break
            (first, iter(cursor) is cursor, list(data), next(cursor), list(cursor), next(cursor, 99), bytes(data) is data)
            """);
        var value = engine.GetGlobal("data");
        Assert.Equal(PyType.Bytes, value.Type);
        Assert.Equal("bytes", PyOperations.TypeName(value));
        Assert.Equal("(97, True, [97, 98, 99], 98, [99], 99, True)", result.Repr());
        var items = PyOperations.Iterate(value);
        Assert.Equal(items.ToArray(), items.ToArray());
    }

    [Theory]
    [InlineData("bytes(range(10**40))")]
    [InlineData("bytes([65] * 1000)")]
    [InlineData("list(b'A' * 1000)")]
    public void ConsumingIterablesChargesExecutionBudget(string source)
    {
        var engine = new PythonEngine(new PythonOptions { MaximumSteps = 50 });
        var error = Assert.Throws<PythonException>(() => engine.Evaluate(source));
        Assert.Equal("RuntimeError", error.ErrorName);
        Assert.Equal("maximum execution steps exceeded", error.Message);
        Assert.Equal("b'abc'", engine.Evaluate("bytes([97, 98, 99])").Repr());
    }
}
