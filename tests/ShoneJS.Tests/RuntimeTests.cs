// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class RuntimeTests
{
    [Theory]
    [InlineData("1 === '1'", false)]
    [InlineData("({}) === ({})", false)]
    [InlineData("let x = {}; x === x", true)]
    [InlineData("NaN === NaN", false)]
    [InlineData("-0 === 0", true)]
    [InlineData("null == undefined", true)]
    [InlineData("'a' < 'b'", true)]
    [InlineData("9007199254740993n > 9007199254740992", true)]
    [InlineData("9007199254740993n == '9007199254740993'", true)]
    [InlineData("Symbol('x') === Symbol('x')", false)]
    public void ComparesValues(string script, bool expected) => Assert.Equal(expected, new Engine().Evaluate(script).Data);

    [Theory]
    [InlineData("const x = 1; x = 2", "TypeError")]
    [InlineData("let x = x", "ReferenceError")]
    [InlineData("typeof x; let x", "ReferenceError")]
    [InlineData("'use strict'; undeclared = 2", "ReferenceError")]
    [InlineData("1n + 1", "TypeError")]
    [InlineData("1n / 0n", "RangeError")]
    [InlineData("1n >>> 0n", "TypeError")]
    [InlineData("2n ** -1n", "RangeError")]
    [InlineData("BigInt.asIntN(-1, 1n)", "RangeError")]
    [InlineData("BigInt.asUintN(0, 1)", "TypeError")]
    [InlineData("BigInt('0x_ff')", "SyntaxError")]
    [InlineData("BigInt('-0x1')", "SyntaxError")]
    public void ReportsJavaScriptError(string source, string errorName)
    {
        var engine = new Engine();
        var script = engine.ParseScript(source);
        Assert.Equal(errorName, Assert.Throws<JsException>(() => engine.Execute(script)).ErrorName);
    }

    [Theory]
    [InlineData("function f(){ return x; var x = 8; } typeof f()", "undefined")]
    [InlineData("{var x = 4;} var x; x", "4")]
    [InlineData("let o = {f: function(){ return this.x; }, x: 4}; o.f()", "4")]
    [InlineData("let order = ''; let o = {}; function key(){ order += 'k'; return 'x'; } function rhs(){ order += 'r'; return 1; } o[key()] = rhs(); order", "kr")]
    [InlineData("let x; try { throw 7; } catch(e) { x = e; } finally { x += 1; } x", "8")]
    [InlineData("9007199254740993n + 10n", "9007199254741003")]
    [InlineData("let x = 0; for (let i = 0; i < 4; i++) { if (i === 2) continue; x += i; } x", "4")]
    [InlineData("do { var x = 4; } while (false) x", "4")]
    [InlineData("let i = 0; let total = 0; do { i++; if (i === 2) continue; if (i === 4) break; total += i; } while (i < 8); total", "4")]
    [InlineData("let o = {valueOf(){return 2n;}}; -o", "-2")]
    [InlineData("`${{ toString(){return 'template';} }}`", "template")]
    [InlineData("let a; let b; for(let i=0;i<2;i++){if(i===0)a=function(){return i;};else b=function(){return i;};} a()*10+b()", "1")]
    [InlineData("let calls = 0; null?.[calls++].x; calls", "0")]
    [InlineData("let calls = 0; let o = {}; o.m?.(calls++); calls", "0")]
    [InlineData("let o = {x: 3, m: function(){return this.x;}}; (o?.m)() + o.m?.()", "6")]
    [InlineData("(0 ?? 4) + (undefined ?? 5)", "5")]
    [InlineData("delete null?.x", "true")]
    [InlineData("2 ** 3 ** 2", "512")]
    [InlineData("(-2) ** 3", "-8")]
    [InlineData("-1 >>> 0", "4294967295")]
    [InlineData("1 << 1 + 2", "8")]
    [InlineData("let x = 3n; x **= 4n; x >>= -2n; x &= 255n; x", "68")]
    [InlineData("let x = 2; -x++ + x", "1")]
    [InlineData("-2n >> 1000000000000n", "-1")]
    [InlineData("BigInt.asIntN(8, 255n)", "-1")]
    [InlineData("BigInt.asUintN(8, -1n)", "255")]
    [InlineData("BigInt.asIntN(9007199254740991, -1n)", "-1")]
    [InlineData("BigInt({valueOf(){return '0x10';}})", "16")]
    [InlineData("BigInt('010') + BigInt('0o10')", "18")]
    [InlineData("Number(9007199254740993n)", "9007199254740992")]
    [InlineData("Number(9007199254740995n)", "9007199254740996")]
    [InlineData("(-9n / 4n) * 10n + (-9n % 4n)", "-21")]
    [InlineData("BigInt.asIntN(8, -128n) + (-128n >> 2n)", "-160")]
    [InlineData("const a = 0; a &&= missing; const b = 1; b ||= missing; const c = false; c ??= missing; a + b", "1")]
    [InlineData("let i = 0; let a = [null]; a[i++] ??= 7; a[0] + i", "8")]
    [InlineData("let a = 0, b = true; a ||= b &&= 4; a + b", "8")]
    [InlineData("({valueOf(){return 2;}}) + 3", "5")]
    [InlineData("let o = {valueOf(){return this;}, toString(){return '4';}}; o == 4", "true")]
    [InlineData("let o = {}; let key = {valueOf(){return 'wrong';}, toString(){return 'right';}}; o[key] = 7; o.right", "7")]
    [InlineData("let s = ''; let a = {valueOf(){s += 'a'; return 1;}}, b = {valueOf(){s += 'b'; return 2;}}; a > b; s", "ab")]
    public void EvaluatesSemantics(string source, string expected) => Assert.Equal(expected, new Engine().Evaluate(source).ToJsString());
}
