// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class ImportModuleTests
{
    [Fact]
    public void ImportsMathAndFromImportsFunctions() =>
        Assert.Equal("42", new PythonEngine().Evaluate("import math as m\nfrom math import sqrt\nint(sqrt(1764) + m.floor(0.9))").ToString());

    [Fact]
    public void MathRoundingReturnsExactIntegers()
    {
        var engine = new PythonEngine();
        Assert.Equal("(1208925819614629174706177, -1, -1)",
            engine.Evaluate("import math\n(math.floor(2 ** 80 + 1), math.ceil(-1.5), math.trunc(-1.5))").Repr());
    }

    [Theory]
    [InlineData("sqrt(-1)", "ValueError")]
    [InlineData("log(0)", "ValueError")]
    [InlineData("sin(math.inf)", "ValueError")]
    [InlineData("floor(math.inf)", "OverflowError")]
    [InlineData("exp(1000)", "OverflowError")]
    public void MathReportsDomainAndOverflowErrors(string expression, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<PythonException>(() =>
            new PythonEngine().Evaluate($"import math\nmath.{expression}")).ErrorName);

    [Fact]
    public void JsonRoundTripsSupportedValues() =>
        Assert.Equal("{'answer': 42, 'ok': True}", new PythonEngine().Evaluate("import json\njson.loads(json.dumps({'answer': 42, 'ok': True}))").ToString());

    [Fact]
    public void JsonPreservesLargeIntegers() =>
        Assert.Equal("1208925819614629174706176", new PythonEngine().Evaluate("import json\njson.loads(json.dumps(2 ** 80))").ToString());

    [Fact]
    public void ReusesManagedModuleWithinAnEngine()
    {
        var engine = new PythonEngine();
        Assert.Equal(PyValue.True, engine.Evaluate("import math as first\nimport math as second\nfirst is second"));
        var other = new PythonEngine();
        other.Evaluate("import math");
        Assert.NotSame(engine.GetGlobal("first").Data, other.GetGlobal("math").Data);
    }

    [Fact]
    public void ImportsSysValues() =>
        Assert.Equal("ShonePy (Python 3.15 target, partial implementation)", new PythonEngine().Evaluate("import sys\nsys.version").ToString());

    [Fact]
    public void SysReportsPythonPlatformNameAndNativeIntegerLimit()
    {
        var engine = new PythonEngine();
        engine.Evaluate("import sys");
        string? expected = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsAndroid() ? "android"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "darwin" : null;
        if (expected is not null) Assert.Equal(expected, engine.Evaluate("sys.platform").ToString());
        Assert.Equal(PyValue.From((long)nint.MaxValue), engine.Evaluate("sys.maxsize"));
    }

    [Fact]
    public void UnknownModuleRaisesModuleNotFoundError() =>
        Assert.Equal("ModuleNotFoundError", Assert.Throws<PythonException>(() => new PythonEngine().Evaluate("import unavailable" )).ErrorName);
}
