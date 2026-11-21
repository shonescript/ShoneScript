// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class RuntimeOptimizationTests
{
    [Fact]
    public void CallsAndConstructorsGiveHostCallbacksIndependentArgumentArrays()
    {
        var engine = new JsEngine();
        var captured = new List<JsValue[]>();
        JsValue Capture(JsValue[] args)
        {
            captured.Add(args);
            return JsValue.From(new JsObject());
        }
        engine.SetGlobal("Host", JsValue.From(new JsFunction((_, args) => Capture(args), 2, Capture)));
        engine.Evaluate("let n = 0; Host(++n, ++n); new Host(++n, ++n);");

        Assert.Equal(2, captured.Count);
        Assert.NotSame(captured[0], captured[1]);
        Assert.Equal(new double[] { 1, 2 }, captured[0].Select(value => value.ToNumber()));
        Assert.Equal(new double[] { 3, 4 }, captured[1].Select(value => value.ToNumber()));
        captured[0][0] = JsValue.From(42d);
        Assert.Equal(3, captured[1][0].ToNumber());
    }

    [Fact]
    public void InvalidCallAndConstructionStillEvaluateArgumentsInOrder()
    {
        var engine = new JsEngine();
        var script = "let trace = ''; try { (0)(trace += 'a', trace += 'b'); } catch {} " +
            "try { new (0)(trace += 'c', trace += 'd'); } catch {} trace";
        Assert.Equal("abcd", engine.Evaluate(script).ToJsString());
    }

    [Fact]
    public void AnAssignmentReferenceKeepsItsReceiverAcrossAwait()
    {
        var engine = new JsEngine();
        engine.SetGlobal("gate", engine.CreatePromise(out var resolve, out _));
        engine.Evaluate("""
            let original = { value: 2 };
            let target = original;
            let evaluations = 0;
            function key() { evaluations++; return 'value'; }
            async function update() { target[key()] += await gate; }
            update();
            target = { value: 10 };
            """);
        resolve(JsValue.From(3d));
        engine.DrainJobs();

        Assert.Equal("5,10,1", engine.Evaluate("[original.value, target.value, evaluations].join(',')").ToJsString());
    }

    [Fact]
    public void OrdinaryPropertyKeysTakeOrderedSnapshots()
    {
        var obj = new JsObject();
        var empty = obj.OwnPropertyKeys();
        obj.Set("z", JsValue.Undefined);
        obj.Set("a", JsValue.Undefined);
        var names = obj.OwnPropertyKeys();
        obj.Set("4294967295", JsValue.Undefined);
        obj.Set("4294967294", JsValue.Undefined);
        obj.Set("10", JsValue.Undefined);
        obj.Set("01", JsValue.Undefined);
        obj.Set("2", JsValue.Undefined);
        obj.Set("0", JsValue.Undefined);
        var mixed = obj.OwnPropertyKeys();
        obj.Delete("a");
        obj.Set("a", JsValue.Undefined);
        obj.Delete("10");

        Assert.Empty(empty);
        Assert.Equal(new[] { "z", "a" }, names);
        Assert.Equal(new[] { "0", "2", "10", "4294967294", "z", "a", "4294967295", "01" }, mixed);
        Assert.Equal(new[] { "0", "2", "4294967294", "z", "4294967295", "01", "a" }, obj.OwnPropertyKeys());
    }

    [Theory]
    [InlineData(-0d, "0")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1e-6, "0.000001")]
    [InlineData(-1.2345678901234567e-6, "-0.0000012345678901234567")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(-1e21, "-1e+21")]
    [InlineData(1e23, "1e+23")]
    [InlineData(1000000000000000128d, "1000000000000000100")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(double.Epsilon, "5e-324")]
    [InlineData(double.MaxValue, "1.7976931348623157e+308")]
    public void NumbersUseJavaScriptShortestRoundTripNotation(double value, string expected)
    {
        var text = JsValue.From(value).ToJsString();
        Assert.Equal(expected, text);
        Assert.Equal(value, JsValue.From(text).ToNumber());
    }

    [Fact]
    public void NumericCoercionTrimsJavaScriptWhitespaceAndKeepsSignedZero()
    {
        Assert.Equal(double.NegativeInfinity, JsValue.From("\ufeff-Infinity\u2029").ToNumber());
        Assert.Equal(16d, JsValue.From("\u00a0 0x10\t\n").ToNumber());
        Assert.Equal(long.MinValue, BitConverter.DoubleToInt64Bits(JsValue.From("\u2000-0\u2028").ToNumber()));
        Assert.True(double.IsNaN(JsValue.From("\u00851\u0085").ToNumber()));
        Assert.Equal("26", new JsEngine().Evaluate("BigInt('\\uFEFF +10 \\u2029') + BigInt('\\t0x10 \\n')").ToJsString());
    }
}
