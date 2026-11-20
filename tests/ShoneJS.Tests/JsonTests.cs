// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class JsonTests
{
    [Theory]
    [InlineData("JSON.parse(12) === 12 && JSON.parse(true) === true && JSON.parse(null) === null")]
    [InlineData("JSON.parse({toString: function() { return '42'; }}) === 42")]
    [InlineData("Object.is(JSON.parse('-0'), -0) && JSON.parse('1e400') === Infinity")]
    [InlineData("let o = JSON.parse('{\"__proto__\":{\"a\":1},\"x\":1,\"x\":2}'); o.x === 2 && o.hasOwnProperty('__proto__') && o.__proto__.a === 1 && Object.getPrototypeOf(o) === Object.prototype")]
    [InlineData("let a = JSON.parse('[1,[2]]'); Array.isArray(a[1]) && Object.getPrototypeOf(a) === Array.prototype")]
    [InlineData("JSON.parse.length === 2 && JSON.stringify.length === 3 && Object.keys(JSON).length === 0")]
    public void ParsesIntoNativeValues(string script) => Assert.True(new Engine().Evaluate(script).ToBoolean());

    [Theory]
    [InlineData("JSON.parse()")]
    [InlineData("JSON.parse('')")]
    [InlineData("JSON.parse('[1,]')")]
    [InlineData("JSON.parse('{\"a\":1,}')")]
    [InlineData("JSON.parse('{a:1}')")]
    [InlineData("JSON.parse('/* comment */ 1')")]
    [InlineData("JSON.parse('01')")]
    [InlineData("JSON.parse('-')")]
    [InlineData("JSON.parse('1.e0')")]
    [InlineData("JSON.parse('1e+')")]
    [InlineData("JSON.parse('true false')")]
    [InlineData("JSON.parse('\u00A0null')")]
    [InlineData("JSON.parse('\"line\\nfeed\"')")]
    [InlineData("JSON.parse('\"\\\\u1X00\"')")]
    public void RejectsInvalidJson(string script) =>
        Assert.Equal("SyntaxError", Assert.Throws<JsException>(() => new Engine().Evaluate(script)).ErrorName);

    [Fact]
    public void PreservesUtf16AndEscapesOnlyWhereRequired()
    {
        var engine = new Engine();
        Assert.Equal("\ud800", engine.Evaluate("""JSON.parse('"\\uD800"')""").ToJsString());
        Assert.Equal("\"\\ud800\"", engine.Evaluate("""JSON.stringify(JSON.parse('"\\uD800"'))""").ToJsString());
        Assert.Equal("\"😀é/<>\\t\\n\\u0001\"", engine.Evaluate("""JSON.stringify(JSON.parse('"😀é/<>\\t\\n\\u0001"'))""").ToJsString());
    }

    [Fact]
    public void WalksReviverFromLeavesToRootAndDeletesProperties()
    {
        var result = new Engine().Evaluate("""
            let keys = '';
            let result = JSON.parse('{"x":[1,2],"y":true}', function(key, value) {
                keys += '[' + key + ']';
                if (key === '0' && this[1] === 2) return undefined;
                if (key === '1') return value * 3;
                if (key === 'y') return undefined;
                if (key === '' && this[''] === value) return value.x;
                return value;
            });
            keys === '[0][1][x][y][]' && result.length === 2 && !result.hasOwnProperty('0') && result[1] === 6;
            """);
        Assert.True(result.ToBoolean());
        Assert.Equal(JsType.Undefined, new Engine().Evaluate("JSON.parse('1', function() {})").Type);
    }

    [Theory]
    [InlineData("JSON.stringify([null, true, -0, NaN, Infinity, -Infinity])", "[null,true,0,null,null,null]")]
    [InlineData("JSON.stringify([, undefined, Symbol('x'), function() {}])", "[null,null,null,null]")]
    [InlineData("JSON.stringify({b: undefined, a: Symbol('x'), f: function() {}, ok: true})", "{\"ok\":true}")]
    [InlineData("JSON.stringify({z: 1, 4: 2, 2: 3, a: 4})", "{\"2\":3,\"4\":2,\"z\":1,\"a\":4}")]
    [InlineData("let child = {v: 1}; JSON.stringify([child, child])", "[{\"v\":1},{\"v\":1}]")]
    [InlineData("let o = Object.create({a: 1}, {b: {value: 2}, c: {value: 3, enumerable: true}}); JSON.stringify(o)", "{\"c\":3}")]
    [InlineData("let o = Object.create({a: 1}, {b: {value: 2}}); o[2] = {}; JSON.stringify(o, ['b', 2, 'a', 'b', null])", "{\"b\":2,\"2\":{},\"a\":1}")]
    [InlineData("let a = [1, 2]; a.x = 3; JSON.stringify(a, ['x'])", "[1,2]")]
    [InlineData("JSON.stringify({x: [1, {}], y: undefined}, null, 2.9)", "{\n  \"x\": [\n    1,\n    {}\n  ]\n}")]
    [InlineData("JSON.stringify([1], null, 'abcdefghijkl')", "[\nabcdefghij1\n]")]
    [InlineData("JSON.stringify([1], null, Infinity)", "[\n          1\n]")]
    [InlineData("JSON.stringify(new Proxy([1, 2], {}))", "[1,2]")]
    public void StringifiesJsValues(string script, string expected) =>
        Assert.Equal(expected, new Engine().Evaluate(script).ToJsString());

    [Theory]
    [InlineData("JSON.stringify()")]
    [InlineData("JSON.stringify(Symbol('x'))")]
    [InlineData("JSON.stringify(function() {})")]
    public void ReturnsUndefinedForOmittedRootValues(string script) =>
        Assert.Equal(JsType.Undefined, new Engine().Evaluate(script).Type);

    [Fact]
    public void CallsToJsonBeforeReplacerWithCorrectReceiversAndKeys()
    {
        var result = new Engine().Evaluate("""
            let object = { x: { toJSON: function(key) { return this === object.x ? key + '!' : 'wrong'; } }, big: 3n };
            let calls = '';
            let text = JSON.stringify(object, function(key, value) {
                calls += '[' + key + ']';
                if (key === '' && this[''] !== object) return undefined;
                if (key === 'x') return this === object ? value + '?' : 'wrong';
                if (key === 'big') return 'converted';
                return value;
            });
            calls === '[][x][big]' && text === '{"x":"x!?","big":"converted"}';
            """);
        Assert.True(result.ToBoolean());
    }

    [Theory]
    [InlineData("JSON.stringify(1n)")]
    [InlineData("JSON.stringify([1n])")]
    [InlineData("let o = {}; o.self = o; JSON.stringify(o)")]
    [InlineData("let a = []; a[0] = a; JSON.stringify(a)")]
    public void RejectsBigIntsAndCycles(string script) =>
        Assert.Equal("TypeError", Assert.Throws<JsException>(() => new Engine().Evaluate(script)).ErrorName);
}
