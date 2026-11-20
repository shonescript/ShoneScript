// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Tests;

public sealed class PyBigFloatModuleTests
{
    [Fact]
    public void ImportConstructionAndDecimalRoundTripRetainPrecision()
    {
        Assert.Equal(PyValue.True, Evaluate("""
            import bigfloat
            text = '0.123456789012345678901234567890123456789012345678901234567890123456789'
            x = BigFloat(text, 256)
            assert bigfloat.BigFloat(str(x), x.precision) == x
            prefix = '0.12345678901234567890123456789012345678901234567890123456789012345'
            assert str(x)[:len(prefix)] == prefix
            assert x != BigFloat(x.to_float(), 256)
            assert BigFloat(x).precision == 256
            assert BigFloat(x, 96).precision == 96
            assert BigFloat(9007199254740993) == BigFloat('9007199254740993')
            assert BigFloat(9007199254740993).precision == 128
            assert BigFloat(0.1) == BigFloat('0.1000000000000000055511151231257827021181583404541015625')
            x.precision == 256
            """));
    }

    [Fact]
    public void ArithmeticRoundsToNearestEvenAtTheHigherOperandPrecision()
    {
        Assert.Equal(PyValue.True, Evaluate("""
            low = BigFloat(1, 64)
            high = BigFloat(3, 256)
            third = low / high
            assert third.precision == 256
            prefix = '0.33333333333333333333333333333333333333333333333333333333333'
            assert str(third)[:len(prefix)] == prefix
            assert third.to_float() == 1.0 / 3.0
            assert (low + high).precision == 256 and (low + high) == BigFloat(4)
            assert (low - high).precision == 256 and (low - high) == BigFloat(-2)
            assert (high * low).precision == 256 and (high * low) == high
            assert (-high).precision == 256 and (+high) == high
            assert BigFloat('1.25', 2) == BigFloat(1)
            assert BigFloat('1.75').with_precision(2) == BigFloat(2)
            assert BigFloat(5, 2).to_float() == 4.0
            assert (BigFloat(1, 2) + BigFloat('0.25', 2)) == BigFloat(1)
            assert BigFloat(9, 257).sqrt().precision == 257
            assert BigFloat(9, 257).sqrt() == BigFloat(3)
            assert pi().precision == 128 and pi(256).precision == 256
            pi_prefix = '3.14159265358979323846264338327950288419716939937510'
            str(pi(256))[:len(pi_prefix)] == pi_prefix
            """));
    }

    [Fact]
    public void SignedZeroInfinityAndNaNUseNumericComparison()
    {
        Assert.Equal(PyValue.True, Evaluate("""
            import math
            negative = BigFloat('-0', 222)
            positive = BigFloat(0)
            assert negative.precision == 222
            assert str(negative.to_float()) == '-0.0'
            assert str(BigFloat(-0.0).to_float()) == '-0.0'
            assert not negative and not positive
            assert negative == positive and not (negative != positive)
            assert negative <= positive and positive <= negative
            assert negative >= positive and positive >= negative
            assert not (negative < positive or negative > positive or positive < negative or positive > negative)
            inf = BigFloat('Infinity')
            assert inf == BigFloat(math.inf, 256)
            assert inf.to_float() == math.inf
            assert BigFloat('-Infinity') < negative < inf
            nan = BigFloat('NaN')
            assert nan and not (nan == nan) and nan != nan
            assert not (nan < positive or nan <= positive or nan > positive or nan >= positive)
            assert not (positive < nan or positive <= nan or positive > nan or positive >= nan)
            math.isnan(nan.to_float())
            """));
    }

    [Theory]
    [InlineData("BigFloat([])", "TypeError")]
    [InlineData("BigFloat()", "TypeError")]
    [InlineData("BigFloat('bad decimal')", "ValueError")]
    [InlineData("BigFloat('1', 2.5)", "TypeError")]
    [InlineData("BigFloat('1', 1)", "ValueError")]
    [InlineData("pi(65537)", "ValueError")]
    [InlineData("BigFloat(1).with_precision(2 ** 120)", "ValueError")]
    [InlineData("BigFloat(1).sqrt(2)", "TypeError")]
    [InlineData("BigFloat(1) / BigFloat('-0')", "ZeroDivisionError")]
    [InlineData("BigFloat(1) + 1.0", "TypeError")]
    [InlineData("1 < BigFloat(2)", "TypeError")]
    [InlineData("BigFloat(1) // BigFloat(1)", "TypeError")]
    [InlineData("{BigFloat(1): 1}", "TypeError")]
    [InlineData("{(BigFloat(1),)}", "TypeError")]
    [InlineData("BigFloat(1).missing", "AttributeError")]
    [InlineData("BigFloat('1e9999999999')", "OverflowError")]
    [InlineData("BigFloat('1e-9999999999')", "OverflowError")]
    [InlineData("BigFloat('1' * 1000000)", "OverflowError")]
    public void InvalidInputsRaiseCatchablePythonErrors(string expression, string errorName)
    {
        Assert.Equal(PyValue.True, Evaluate($"""
            caught = False
            try:
                {expression}
            except {errorName}:
                caught = True
            caught
            """));
    }

    [Fact]
    public void FormattingMagnitudeLimitRaisesOverflowError()
    {
        Assert.Equal(PyValue.True, Evaluate("""
            x = BigFloat('1e1000')
            for i in range(7):
                x = x * x
            caught = False
            try:
                str(x)
            except OverflowError:
                caught = True
            caught
            """));
    }

    [Theory]
    [InlineData("1e60000")]
    [InlineData("1e-60000")]
    public void ArithmeticMagnitudeLimitRaisesOverflowError(string text)
    {
        Assert.Equal(PyValue.True, Evaluate($"""
            x = BigFloat('{text}')
            x = x * x
            x = x * x
            caught = False
            try:
                x * x
            except OverflowError:
                caught = True
            caught
            """));
    }

    [Fact]
    public void ExistingFloatAndMathBehaviorIsPreserved()
    {
        Assert.Equal(PyValue.True, Evaluate("""
            import math
            assert str(1.0 / 3.0) == '0.3333333333333333'
            assert math.pi == 3.141592653589793
            assert math.sqrt(9.0) == 3.0
            assert 9007199254740993 > 9007199254740992.0
            assert 1 == 1.0 and BigFloat(1) != 1.0
            caught = False
            try:
                math.sqrt(BigFloat(9))
            except TypeError:
                caught = True
            caught
            """));
    }

    private static PyValue Evaluate(string source) => new PythonEngine().Evaluate("from bigfloat import BigFloat, pi\n" + source);
}
