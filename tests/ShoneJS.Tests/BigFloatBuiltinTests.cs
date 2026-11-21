// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class BigFloatBuiltinTests
{
    private static JsEngine CreateEngine() => new(new JsEngineOptions { EnableBigFloat = true });

    [Fact]
    public void GlobalIsOptInAndScopedToAnEngine()
    {
        var disabled = new JsEngine();
        var enabled = CreateEngine();
        var another = CreateEngine();
        Assert.True(disabled.Evaluate("typeof BigFloat === 'undefined' && !('BigFloat' in globalThis)").ToBoolean());
        Assert.True(enabled.Evaluate("typeof BigFloat === 'function' && BigFloat === globalThis.BigFloat").ToBoolean());
        Assert.NotSame(enabled.GetGlobal("BigFloat").Data, another.GetGlobal("BigFloat").Data);
        enabled.Evaluate("BigFloat.pi = 0");
        Assert.Equal("function", another.Evaluate("typeof BigFloat.pi").ToString());

        var interpreter = new Interpreter();
        var source = "typeof BigFloat";
        Assert.Equal("undefined", interpreter.Execute(new Parser(new Lexer(source).Scan()).Parse()).ToString());
    }

    [Fact]
    public void ParsesAndRoundTripsLongDecimals()
    {
        var engine = CreateEngine();
        var result = engine.Evaluate("""
            const input = '0.12345678901234567890123456789012345678901234567890123456789';
            const value = BigFloat(input, 256);
            const text = value.toString();
            BigFloat(text, value.precision).equals(value) && !BigFloat(value.toNumber(), 256).equals(value)
                && String(value) === text;
            """);
        Assert.True(result.ToBoolean());
        Assert.StartsWith("0.12345678901234567890123456789012345678901234567890123456789", engine.Evaluate("text").ToString());
    }

    [Fact]
    public void DividesAtHigherOperandPrecisionAndConvertsExplicitlyToNumber()
    {
        var engine = CreateEngine();
        var text = engine.Evaluate("const third = BigFloat('1', 128).div(BigFloat('3', 256)); third.toString()").ToString();
        Assert.StartsWith("0.3333333333333333333333333333333333333333333333333333333333333333333333", text);
        Assert.Equal(256d, engine.Evaluate("third.precision").ToNumber());
        Assert.Equal(1d / 3d, engine.Evaluate("third.toNumber()").ToNumber());
    }

    [Fact]
    public void UsesNearestEvenRoundingAndPreservesPrecisionAcrossCopiesAndOperations()
    {
        Assert.True(CreateEngine().Evaluate("""
            const original = BigFloat('1.25', 80);
            const copy = BigFloat(original);
            const a = BigFloat(9, 80);
            const b = BigFloat(2n, 200);
            BigFloat('1.25', 2).toNumber() === 1 && BigFloat(1.75, 2).toNumber() === 2
                && BigFloat(5n, 2).toNumber() === 4 && original.withPrecision(2).toNumber() === 1
                && original.precision === 80 && copy.precision === 80 && copy.equals(original) && copy !== original
                && BigFloat(original, 200).precision === 200 && BigFloat(1).precision === 128
                && a.sqrt().precision === 80 && a.sqrt().toNumber() === 3
                && a.add(b).precision === 200 && a.add(b).toNumber() === 11
                && b.mul(a).precision === 200 && b.mul(a).toNumber() === 18
                && a.sub(b).precision === 200 && a.sub(b).toNumber() === 7
                && BigFloat(1, 65536).precision === 65536;
            """).ToBoolean());
    }

    [Fact]
    public void ConvertsBigIntExactlyAndNumberAsBinary64()
    {
        Assert.True(CreateEngine().Evaluate("""
            const integer = BigFloat(9007199254740993n);
            integer.equals(BigFloat('9007199254740993')) && integer.toNumber() === 9007199254740992
                && BigFloat(0.1).equals(BigFloat('0.1000000000000000055511151231257827021181583404541015625'))
                && !BigFloat(0.1).equals(BigFloat('0.1'));
            """).ToBoolean());
    }

    [Fact]
    public void SupportsIeeeSpecialValuesAndSignedZero()
    {
        var engine = CreateEngine();
        Assert.True(engine.Evaluate("""
            const zero = BigFloat(0);
            const negativeZero = BigFloat(-0, 180);
            const nan = BigFloat(NaN);
            const infinity = BigFloat(Infinity);
            zero.equals(negativeZero) && 1 / negativeZero.toNumber() === -Infinity
                && 1 / BigFloat('-0').toNumber() === -Infinity
                && !nan.equals(nan) && nan === nan && nan.toNumber() !== nan.toNumber()
                && infinity.equals(BigFloat('Infinity')) && !infinity.equals(BigFloat(-Infinity))
                && BigFloat(1).div(zero).toNumber() === Infinity
                && BigFloat(1).div(negativeZero).toNumber() === -Infinity
                && BigFloat(-1).sqrt().equals(nan) === false;
            """).ToBoolean());
        Assert.True(double.IsNaN(engine.Evaluate("zero.div(zero).toNumber()").ToNumber()));
        Assert.True(double.IsNaN(engine.Evaluate("BigFloat(-1).sqrt().toNumber()").ToNumber()));
    }

    [Fact]
    public void InstancePropertiesAreImmutable()
    {
        Assert.True(CreateEngine().Evaluate("""
            const value = BigFloat(3, 80);
            value.extra = 42;
            value.precision = 200;
            value instanceof BigFloat && value.extra === undefined && value.precision === 80 && Object.isFrozen(value);
            """).ToBoolean());
    }

    [Theory]
    [InlineData("BigFloat(true)", "TypeError")]
    [InlineData("BigFloat({ toString() { return '1'; } })", "TypeError")]
    [InlineData("BigFloat()", "TypeError")]
    [InlineData("BigFloat(1, '128')", "TypeError")]
    [InlineData("BigFloat(1, 1)", "RangeError")]
    [InlineData("BigFloat(1, 128.5)", "RangeError")]
    [InlineData("BigFloat(1, NaN)", "RangeError")]
    [InlineData("BigFloat.pi(Infinity)", "RangeError")]
    [InlineData("BigFloat.pi(65537)", "RangeError")]
    [InlineData("BigFloat(1).withPrecision()", "TypeError")]
    [InlineData("BigFloat('0x10')", "SyntaxError")]
    [InlineData("BigFloat('1.2.3')", "SyntaxError")]
    [InlineData("BigFloat('1e999999999999')", "RangeError")]
    [InlineData("BigFloat('1e-999999999999')", "RangeError")]
    [InlineData("BigFloat(1).add(1)", "TypeError")]
    [InlineData("BigFloat(1).equals(1)", "TypeError")]
    [InlineData("BigFloat.prototype.sqrt()", "TypeError")]
    [InlineData("BigFloat.prototype.precision", "TypeError")]
    [InlineData("BigFloat(1) + BigFloat(2)", "TypeError")]
    [InlineData("BigFloat(1) + '2'", "TypeError")]
    [InlineData("2 * BigFloat(1)", "TypeError")]
    [InlineData("-BigFloat(1)", "TypeError")]
    [InlineData("Number(BigFloat(1))", "TypeError")]
    public void ReportsCatchableScriptErrors(string expression, string expectedError)
    {
        var result = CreateEngine().Evaluate($$"""
            let errorType;
            try { {{expression}}; } catch (e) { errorType = e instanceof {{expectedError}} && e.name; }
            errorType;
            """);
        Assert.Equal(expectedError, result.ToString());
    }

    [Fact]
    public void LimitsParsingLengthAndFormattingMagnitude()
    {
        var engine = CreateEngine();
        engine.SetGlobal("longText", JsValue.From(new string('1', 100_000)));
        Assert.True(engine.Evaluate("""
            let parseLimited = false;
            let formattingLimited = false;
            try { BigFloat(longText); } catch (e) { parseLimited = e instanceof RangeError; }
            const value = BigFloat('1e10000');
            const square = value.mul(value);
            const fourth = square.mul(square);
            try { fourth.mul(fourth).toString(); } catch (e) { formattingLimited = e instanceof RangeError; }
            parseLimited && formattingLimited;
            """).ToBoolean());
    }

    [Fact]
    public void RejectsArithmeticPastTheExponentLimit()
    {
        Assert.True(CreateEngine().Evaluate("""
            let caught = false;
            try {
                let value = BigFloat('1e60000');
                for (let i = 0; i < 4; i++) value = value.mul(value);
            } catch (e) { caught = e instanceof RangeError; }
            caught;
            """).ToBoolean());
    }

    [Fact]
    public void ExecutesBigFloatInTypeScript()
    {
        var source = "const bits: number = 256; BigFloat.pi(bits).toString()";
        var result = CreateEngine().EvaluateTypeScript(source);
        Assert.StartsWith("3.14159265358979323846264338327950288419716939937510", result.ToString());
        Assert.Equal("ReferenceError", Assert.Throws<JsException>(() => new JsEngine().EvaluateTypeScript(source)).ErrorName);
    }
}
