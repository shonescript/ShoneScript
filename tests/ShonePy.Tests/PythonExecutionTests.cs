// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PythonExecutionTests
{
    [Theory]
    [InlineData("-7 // 3", "-3")]
    [InlineData("-7 % 3", "2")]
    [InlineData("2 ** 80", "1208925819614629174706176")]
    [InlineData("{True: 42}[1]", "42")]
    [InlineData("'😀x'[1]", "x")]
    [InlineData("[0, 1, 2, 3, 4][1:4:2]", "[1, 3]")]
    [InlineData("[0, 1, 2, 3, 4][::-1]", "[4, 3, 2, 1, 0]")]
    [InlineData("(0, 1, 2, 3, 4)[-100000000000000000000:100000000000000000000]", "(0, 1, 2, 3, 4)")]
    [InlineData("'a😀bc'[::-2]", "c😀")]
    [InlineData("x = [0]\ny = x\ny[0] = 42\nx[0]", "42")]
    [InlineData("a, b = 40, 2; a + b", "42")]
    [InlineData("def values(): return 40, 2;\na, b = values()\na + b", "42")]
    [InlineData("x = 0\nfor a, b in (40, 2), : x = a; x += b\nx", "42")]
    [InlineData("x = 40\ndef f(a, b=x): return a + b\nx = 0\nf(2)", "42")]
    [InlineData("def f(items=[]):\n    items.append(21)\n    return sum(items)\nf(); f()", "42")]
    [InlineData("def f(a, b=2): return a + b\nf(b=1, a=41)", "42")]
    [InlineData("def f(a, b=2): return a + b\nf(a=40)", "42")]
    [InlineData("def f(a=42): return a is None\nf(a=None)", "True")]
    [InlineData("def f(a, /, b=2, *items, c, d=5, **extra): return a + b + items[0] + c + d + extra['x']\nf(1, 2, 3, c=4, x=27)", "42")]
    [InlineData("def f(a, /, **kwargs): return kwargs['a']\nf(1, a=42)", "42")]
    [InlineData("def f(a, *, b=42): return b\nf(1)", "42")]
    [InlineData("def f(*args): return args\nf(1, 2)", "(1, 2)")]
    [InlineData("def f(a, b, c): return a + b + c\nf(*(1, 2), c=39)", "42")]
    [InlineData("def f(a, b): return a + b\nf(**{'a': 40, 'b': 2})", "42")]
    [InlineData("def f(a, b): return a + b\nf(1, **{'b': 41})", "42")]
    [InlineData("def f(a, b): return a + b\nf(**{'a': 1}, **{'b': 41})", "42")]
    [InlineData("x = 'if'; x", "if")]
    [InlineData("int(missing if False else 42 if False or True else missing)", "42")]
    [InlineData("[(False or True), 42 if True else missing]", "[True, 42]")]
    [InlineData("1 < 2 < 3", "True")]
    [InlineData("1 < 2 > 3", "False")]
    [InlineData("False and missing", "False")]
    [InlineData("x = (\r\n    40 +\r\n    2\r\n)\r\nx", "42")]
    public void EvaluatesPythonExpressions(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).ToString());

    [Fact]
    public void SliceStepCannotBeZero()
    {
        var error = Assert.Throws<PythonException>(() => new PythonEngine().Evaluate("[1, 2][::0]"));
        Assert.Equal("ValueError", error.ErrorName);
    }

    [Theory]
    [InlineData("[1, 2][None:None:None]", "[1, 2]")]
    [InlineData("[1, 2][None:None:-1]", "[2, 1]")]
    [InlineData("[1, 2][::-100000000000000000000]", "[2]")]
    public void SliceUsesPythonDefaultsAndClampsLargeSteps(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).ToString());

    [Theory]
    [InlineData("f(1, a=2)", "TypeError")]
    [InlineData("f(b=2)", "TypeError")]
    [InlineData("f(a=1, a=2)", "SyntaxError")]
    [InlineData("f(a=1, 2)", "SyntaxError")]
    [InlineData("def f(a): return a\nf(1, **{'a': 2})", "TypeError")]
    [InlineData("def f(a): return a\nf(**{1: 2})", "TypeError")]
    [InlineData("def f(a): return a\nf(**[('a', 1)])", "TypeError")]
    [InlineData("def f(a): return a\nf(*42)", "TypeError")]
    [InlineData("def f(a, /): return a\nf(a=1)", "TypeError")]
    [InlineData("def f(*, a): return a\nf()", "TypeError")]
    [InlineData("def f(a, /, **kw): return kw['a']\nf(a=42)", "TypeError")]
    public void RejectsInvalidArgumentBinding(string call, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<PythonException>(() =>
            new PythonEngine().Evaluate($"def f(a): return a\n{call}")).ErrorName);

    [Theory]
    [InlineData("global x; x = 42; x", "42")]
    [InlineData("x = 40\ndef f():\n    global x\n    x += 2\nf()\nx", "42")]
    [InlineData("def outer():\n    value = 40\n    def inner():\n        nonlocal value\n        value += 2\n    inner()\n    return value\nouter()", "42")]
    public void BindsGlobalAndNonlocalNames(string source, string expected) =>
        Assert.Equal(expected, new PythonEngine().Evaluate(source).ToString());

    [Theory]
    [InlineData("def f():\n    nonlocal missing\n    pass\nf()", "SyntaxError")]
    [InlineData("def f():\n    global value\n    value = 1\n    nonlocal value\nf()", "SyntaxError")]
    [InlineData("nonlocal value", "SyntaxError")]
    [InlineData("value = 1\ndef f():\n    nonlocal value", "SyntaxError")]
    public void RejectsInvalidScopeDeclarations(string source, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => new PythonEngine().Evaluate(source)).ErrorName);

    [Fact]
    public void EvaluatesStarredArgumentsBeforeKeywords()
    {
        const string source = """
            def mark(n, value): print(n); return value
            def f(*args, **kwargs): pass
            f(x=mark(1, 1), *mark(2, []), **mark(3, {}))
            """;
        using var output = new StringWriter();
        new PythonEngine(new PythonOptions { Output = output }).Evaluate(source);
        Assert.Equal($"2{Environment.NewLine}1{Environment.NewLine}3{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public void ExecutesLoopsAndClosures()
    {
        const string source = """
            def outer(x):
                def add(y):
                    return x + y
                return add
            add = outer(40)
            total = 0
            for i in range(3):
                if i == 1:
                    continue
                total += i
            print(add(total))
            add(total)
            """;
        using var output = new StringWriter();
        var engine = new PythonEngine(new PythonOptions { Output = output });
        Assert.Equal("42", engine.Evaluate(source).ToString());
        Assert.Equal("42" + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void SyntaxErrorsPreventEarlierStatementsFromExecuting()
    {
        var engine = new PythonEngine();
        engine.SetGlobal("answer", PyValue.From(40));
        var error = Assert.Throws<PythonException>(() => engine.Evaluate("answer = 42\nif True:\n", "sample.py"));
        Assert.Contains("Error", error.ErrorName);
        Assert.Equal("sample.py", error.FileName);
        Assert.Equal("40", engine.GetGlobal("answer").ToString());
    }
}
