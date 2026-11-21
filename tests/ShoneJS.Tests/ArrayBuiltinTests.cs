// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class ArrayBuiltinTests
{
    [Theory]
    [InlineData("Array.isArray([]) && Array.isArray(Array.prototype) && Object.getPrototypeOf([]) === Array.prototype")]
    [InlineData("Object.getPrototypeOf(Array.prototype) === Object.prototype && [].constructor === Array && Array.name === 'Array' && Array.length === 1")]
    [InlineData("Array().length === 0 && Object.getPrototypeOf(new Array()) === Array.prototype")]
    [InlineData("let a = new Array(3); a.length === 3 && !Reflect.has(a, 0) && !Reflect.has(a, 2)")]
    [InlineData("let a = Array('3'); let b = new Array(3, 4); a.length === 1 && a[0] === '3' && b.length === 2 && b[1] === 4")]
    [InlineData("Array(4294967295).length === 4294967295 && Array(-0).length === 0 && !Object.is(Array(-0).length, -0)")]
    [InlineData("!Array.isArray() && !Array.isArray(null) && !Array.isArray({length: 0}) && !Array.isArray(Object.create(Array.prototype))")]
    [InlineData("let fail = function() { throw 1; }; Array.isArray(new Proxy(new Proxy([], {get: fail}), {getPrototypeOf: fail})) && !Array.isArray(new Proxy({}, {}))")]
    [InlineData("let d = Object.getOwnPropertyDescriptor(Array.prototype, 'map'); d.writable && d.configurable && !d.enumerable && d.value.name === 'map' && d.value.length === 1")]
    public void InstallsRealmArrayConstructor(string script) =>
        Assert.True(new JsEngine().Evaluate(script).ToBoolean());

    [Theory]
    [InlineData("Array(-1)", "RangeError")]
    [InlineData("new Array(1.5)", "RangeError")]
    [InlineData("Array(4294967296)", "RangeError")]
    [InlineData("Array(NaN)", "RangeError")]
    [InlineData("Array(Infinity)", "RangeError")]
    [InlineData("let pair = Proxy.revocable([], {}); pair.revoke(); Array.isArray(pair.proxy)", "TypeError")]
    [InlineData("let push = Array.prototype.push; push(1)", "TypeError")]
    [InlineData("Array.prototype.map(0)", "TypeError")]
    [InlineData("let a = []; a.constructor = null; a.slice()", "TypeError")]
    [InlineData("Array(3).reduce(function(x, y) { return x + y; })", "TypeError")]
    [InlineData("Object.freeze([]).pop()", "TypeError")]
    [InlineData("Object.seal([0]).pop()", "TypeError")]
    [InlineData("let a = {length: 9007199254740991, push: Array.prototype.push}; a.push(1)", "TypeError")]
    public void RejectsInvalidArrayOperations(string script, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<JsException>(() => new JsEngine().Evaluate(script)).ErrorName);

    [Theory]
    [InlineData("let a = [, 2]; a.push(3) === 3 && a.pop() === 3 && a.unshift(1) === 3 && !a.hasOwnProperty(1) && a.shift() === 1 && a.length === 2 && !a.hasOwnProperty(0) && a[1] === 2")]
    [InlineData("let a = [, 2, 3]; a.reverse() === a && a.length === 3 && a[0] === 3 && a[1] === 2 && !a.hasOwnProperty(2)")]
    [InlineData("let a = [0, , 2, 3]; let b = a.splice(1, 2, 'x'); a.join() === '0,x,3' && b.length === 2 && !b.hasOwnProperty(0) && b[1] === 2")]
    [InlineData("let a = [0, , 2, 3]; let b = a.splice(1, 1, 'x', 'y'); a.join() === '0,x,y,2,3' && b.length === 1 && !b.hasOwnProperty(0)")]
    [InlineData("let a = [0, 1, 2]; let b = a.splice(); let c = a.splice(1); b.length === 0 && c.join() === '1,2' && a.join() === '0'")]
    [InlineData("let a = [0, , 2, 3].slice(-3, -1); a.length === 2 && !a.hasOwnProperty(0) && a[1] === 2 && Object.getPrototypeOf(a) === Array.prototype")]
    [InlineData("let a = [1, , 3].concat([, 5], 6); a.length === 6 && !a.hasOwnProperty(1) && !a.hasOwnProperty(3) && a[4] === 5 && a[5] === 6")]
    [InlineData("[, null, undefined, 4, [5, 6]].join('-') === '---4-5,6'")]
    [InlineData("let a = []; a.push(a); a.toString() === ''")]
    [InlineData("let a = []; a.join = 1; a.toString() === '[object Array]'")]
    [InlineData("let a = []; a.join = function() { return this === a; }; a.toString()")]
    [InlineData("let a = []; let read = 0; Object.defineProperty(a, 'constructor', {get: function() { read = read + 1; }}); Object.getPrototypeOf(a.map(function() {})) === Array.prototype && read === 1")]
    [InlineData("let a = Array(4294967295); a[4294967294] = 'last'; a.pop() === 'last' && a.length === 4294967294")]
    [InlineData("let a = [, NaN, -0, 3, 3]; a.includes(undefined) && a.indexOf(undefined) === -1 && a.includes(NaN) && a.indexOf(NaN) === -1 && a.includes(0) && a.indexOf(3, -1) === 4 && a.lastIndexOf(3) === 4 && a.lastIndexOf(3, undefined) === -1 && a.lastIndexOf(3, -2) === 3")]
    public void HandlesSparseArrayOperations(string script) =>
        Assert.True(new JsEngine().Evaluate(script).ToBoolean());

    [Theory]
    [InlineData("let a = [, 2, , 4]; let seen = ''; let context = {}; let value = a.findLast(function(v, i, source) { if (source !== a || this !== context) throw 'bad'; seen = seen + i; return i === 1; }, context); value === 2 && seen === '321'")]
    [InlineData("let a = [, 2, , 4]; let seen = ''; a.findLastIndex(function(v, i) { seen = seen + i; return false; }) === -1 && seen === '3210'")]
    [InlineData("let a = [, 3, 1].toReversed(); a.length === 3 && a[0] === 1 && a[1] === 3 && a[2] === undefined && a.hasOwnProperty(2)")]
    [InlineData("let a = [10, 2, 1, 2].toSorted(); a.join() === '1,10,2,2' && [3, 1, 2].toSorted(function(a, b) { return a - b; }).join() === '1,2,3'")]
    [InlineData("let a = [3, 1, 2].toSorted(function(a, b) { return 0; }); a.join() === '3,1,2'")]
    [InlineData("let a = [0, , 2, 3].toSpliced(1, 2, 'x'); a.join() === '0,x,3' && a.hasOwnProperty(1)")]
    [InlineData("let a = [, 2, 3].with(-1, 9); a.join() === ',2,9' && a.hasOwnProperty(0) && a[0] === undefined")]
    [InlineData("let log = ''; let a = [1, 2, 3]; let b = a.toSpliced({valueOf: function() { log = log + 's'; return 1; }}, {valueOf: function() { log = log + 'd'; return 1; }}, 8); log === 'sd' && b.join() === '1,8,3' && a.join() === '1,2,3'")]
    [InlineData("let log = ''; let a = [1, 2]; let b = a.with({valueOf: function() { log = log + 'i'; return 0; }}, 9); log === 'i' && b.join() === '9,2' && a.join() === '1,2'")]
    public void SupportsChangeArrayByCopyMethods(string script) =>
        Assert.True(new JsEngine().Evaluate(script).ToBoolean());

    [Theory]
    [InlineData("[].findLast(1)")]
    [InlineData("[].findLastIndex(null)")]
    [InlineData("[].toSorted(1)")]
    public void RejectsInvalidNewArrayMethodArguments(string script) =>
        Assert.Equal("TypeError", Assert.Throws<JsException>(() => new JsEngine().Evaluate(script)).ErrorName);

    [Fact]
    public void WithOutOfRangeIndexThrowsRangeError() =>
        Assert.Equal("RangeError", Assert.Throws<JsException>(() => new JsEngine().Evaluate("[1].with(1, 2)")).ErrorName);

    [Fact]
    public void CallbackMethodsPreserveHolesOrVisitThemAsSpecified()
    {
        var result = new JsEngine().Evaluate("""
            let a = [, 2, , 4];
            let context = {n: 3};
            let called = '';
            let mapped = a.map(function(v, i, source) {
                if (source !== a || this !== context) throw 'bad callback arguments';
                called = called + i;
                return v * this.n;
            }, context);
            let filtered = a.filter(function(v) { return v > 2; });
            called === '13' && mapped.length === 4 && !mapped.hasOwnProperty(0) && mapped[1] === 6 &&
                !mapped.hasOwnProperty(2) && mapped[3] === 12 && filtered.join() === '4' &&
                a.every(function(v) { return v > 0; }) && a.some(function(v) { return v === 2; }) &&
                a.find(function(v, i) { return i === 0; }) === undefined &&
                a.findIndex(function(v) { return v === undefined; }) === 0 &&
                a.reduce(function(x, y) { return x + y; }) === 6 &&
                a.reduceRight(function(x, y) { return x - y; }) === 2;
            """);
        Assert.True(result.ToBoolean());
    }

    [Fact]
    public void IterationCapturesLengthAndRechecksIndexedProperties()
    {
        var result = new JsEngine().Evaluate("""
            let a = [1, 2, , 4];
            let seen = [];
            a.forEach(function(value, index) {
                seen.push(index + ':' + value);
                if (index === 0) {
                    Reflect.deleteProperty(a, '1');
                    a[2] = 3;
                    a.push(5);
                }
            });
            seen.join() === '0:1,2:3,3:4';
            """);
        Assert.True(result.ToBoolean());
    }

    [Fact]
    public void MethodsUseArrayLikeReceiversAndInheritedIndices()
    {
        var result = new JsEngine().Evaluate("""
            let proto = {1: 'B'};
            let source = Object.create(proto);
            source[0] = 'A';
            source.length = {valueOf: function() { return 2.8; }};
            source.slice = Array.prototype.slice;
            source.push = Array.prototype.push;
            let copy = source.slice();
            copy.join() === 'A,B' && copy.hasOwnProperty(1) && source.push('C') === 3 && source[2] === 'C';
            """);
        Assert.True(result.ToBoolean());
    }

    [Fact]
    public void PrimitiveStringReceiversExposeCharactersAndBoxedCallbackReceiver()
    {
        var engine = new JsEngine();
        var map = Assert.IsType<JsFunction>(engine.Evaluate("Array.prototype.map").Data);
        var callback = new JsFunction(args =>
        {
            var receiver = Assert.IsAssignableFrom<JsObject>(args[2].Data);
            Assert.Equal(2d, receiver.Get("length").ToNumber());
            Assert.False(receiver.GetOwnProperty("length")!.Writable);
            return args[0];
        });
        var result = Assert.IsType<JsArray>(map.Call(JsValue.From("AB"), [JsValue.From(callback)]).Data);
        Assert.Equal("A", result.Get("0").ToJsString());
        Assert.Equal("B", result.Get("1").ToJsString());
    }
}
