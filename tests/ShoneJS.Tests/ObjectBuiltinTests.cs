// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneScript.Runtime;

namespace ShoneScript.Tests;

public class ObjectBuiltinTests
{
    [Theory]
    [InlineData("Object.keys({z: 1, 4: 2, 2: 3, a: 4})[0]", "2")]
    [InlineData("Object.values({z: 1, 4: 2, 2: 3, a: 4})[1]", "2")]
    [InlineData("Object.entries({z: 1, 4: 2, 2: 3, a: 4})[2][0]", "z")]
    [InlineData("Object.entries('AB')[1][1]", "B")]
    [InlineData("Reflect.ownKeys([1, 2])[2]", "length")]
    [InlineData("Object.getOwnPropertyNames('AB')[2]", "length")]
    public void EnumeratesOwnKeysInOrder(string script, string expected) =>
        Assert.Equal(expected, new Engine().Evaluate(script).ToJsString());

    [Theory]
    [InlineData("let p = {inherited: 1}; let o = Object.create(p, {own: {value: 2, enumerable: true}, hidden: {value: 3}}); Object.keys(o).length === 1 && Object.keys(o)[0] === 'own' && o.hidden === 3 && o.inherited === 1")]
    [InlineData("let o = Object.create(null); Object.getPrototypeOf(o) === null && Reflect.setPrototypeOf(o, Object.prototype) && o.hasOwnProperty('missing') === false")]
    [InlineData("let o = {}; let p = Object.create(o); Reflect.setPrototypeOf(o, p) === false && Object.getPrototypeOf(o) === Object.prototype")]
    [InlineData("Object.is(NaN, NaN) && !Object.is(0, -0) && Object.is(-0, -0)")]
    [InlineData("let d = Object.create({enumerable: true}); d.value = 4; let o = {}; Object.defineProperty(o, 'x', d); let result = Reflect.getOwnPropertyDescriptor(o, 'x'); result.value === 4 && result.enumerable && !result.writable && !result.configurable")]
    [InlineData("let o = {}; Object.defineProperty(o, 'x', {get: function() { return 10; }}); let d = Object.getOwnPropertyDescriptors(o).x; typeof d.get === 'function' && d.hasOwnProperty('set') && !d.hasOwnProperty('value')")]
    [InlineData("let o = {}; Object.defineProperty(o, 'x', {value: 1}); Reflect.defineProperty(o, 'x', {value: 2}) === false && Reflect.deleteProperty(o, 'x') === false && Reflect.set(o, 'x', 2) === false")]
    [InlineData("let proto = {}; Object.defineProperty(proto, 'x', {get: function() { return this.stored; }, set: function(v) { this.stored = v; }}); let obj = Object.create(proto); let receiver = {}; Reflect.set(obj, 'x', 7, receiver) && Reflect.get(obj, 'x', receiver) === 7 && !obj.hasOwnProperty('stored') && Reflect.has(obj, 'x')")]
    [InlineData("let o = {}; let key = {toString: function() { return 'x'; }}; Reflect.set(o, key, 5) && Reflect.get(o, key) === 5")]
    [InlineData("let o = {x: 1}; Object.freeze(o) === o && Object.isFrozen(o) && Object.isSealed(o) && !Reflect.isExtensible(o) && !Reflect.set(o, 'x', 2) && !Reflect.set(o, 'y', 3)")]
    [InlineData("let o = {x: 1}; Object.seal(o) === o && Object.isSealed(o) && !Object.isFrozen(o) && Reflect.set(o, 'x', 2) && !Reflect.deleteProperty(o, 'x')")]
    [InlineData("Object.getPrototypeOf(new Object()) === Object.prototype && Object.getPrototypeOf({}) === Object.prototype")]
    [InlineData("function f(a){return this.x+a;} Reflect.apply(f, {x:2}, {0:3,length:1}) === 5")]
    [InlineData("function F(x){this.x=x;} function G(){} let o=Reflect.construct(F,[4],G); o instanceof G && o.x===4")]
    [InlineData("function F(){} F.prototype=0; Object.getPrototypeOf(new F()) === Object.prototype")]
    public void ImplementsDescriptorPrototypeAndReflectOperations(string script) =>
        Assert.True(new Engine().Evaluate(script).ToBoolean());

    [Fact]
    public void EnumerableValuesRespectMutationsDuringIteration()
    {
        var result = new Engine().Evaluate("""
            let source = {};
            Object.defineProperty(source, 'first', { enumerable: true, get: function() {
                Reflect.deleteProperty(source, 'second');
                source.third = 3;
                return 1;
            }});
            source.second = 2;
            let keys = Object.keys(source);
            let values = Object.values(source);
            keys.length === 2 && values.length === 1 && values[0] === 1 && source.third === 3;
            """);
        Assert.True(result.ToBoolean());
    }

    [Fact]
    public void DefinePropertiesConvertsAllDescriptorsBeforeDefiningAny()
    {
        var result = new Engine().Evaluate("""
            let target = {};
            try { Object.defineProperties(target, { first: { value: 1 }, second: { get: 2 } }); }
            catch (e) { }
            Object.getOwnPropertyNames(target).length;
            """);
        Assert.Equal(0d, result.ToNumber());
    }

    [Theory]
    [InlineData("Object.keys(null)")]
    [InlineData("Object.create(1)")]
    [InlineData("Object.defineProperty({}, 'x', {writable: true, get: undefined})")]
    [InlineData("Object.defineProperty(Object.freeze({}), 'new', {})")]
    [InlineData("Reflect.get('a', 0)")]
    [InlineData("Reflect.set({}, Symbol('x'), 1)")]
    [InlineData("Reflect.get({}, {toString: function() { return Symbol('x'); }})")]
    [InlineData("Object.setPrototypeOf(Object.prototype, {})")]
    public void RejectsInvalidArgumentsWithTypeError(string script) =>
        Assert.Equal("TypeError", Assert.Throws<JsException>(() => new Engine().Evaluate(script)).ErrorName);
}
