// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneScript.Runtime;

namespace ShoneScript.Tests;

public class ProxyTests
{
    [Theory]
    [InlineData("typeof Proxy === 'function' && Proxy.name === 'Proxy' && Proxy.length === 2 && Proxy.prototype === undefined && Proxy.revocable.length === 2")]
    [InlineData("let t = {x: 1}; let p = new Proxy(t, {}); p.x = 3; p.y = 4; delete p.x; p !== t && typeof p === 'object' && t.y === 4 && !('x' in t) && Object.getPrototypeOf(p) === Object.prototype && Object.keys(p)[0] === 'y'")]
    [InlineData("let t = {x: 1}; let h = Object.create({get: function(target, key, receiver) { return this === h && target === t && key === 'x' && receiver === p; }}); let p = new Proxy(t, h); p.x && (h.get = null) === null && p.x === 1")]
    [InlineData("let t = {}; Object.defineProperty(t, 'x', {get: function() { return this.y; }, set: function(v) { this.y = v; }}); let p = new Proxy(t, {}); let r = {}; Reflect.set(p, 'x', 8, r) && Reflect.get(p, 'x', r) === 8 && !('y' in t)")]
    [InlineData("let t = Object.freeze({x: NaN}); let p = new Proxy(t, {get: function() { return NaN; }, set: function() { return true; }}); Object.is(p.x, NaN) && Reflect.set(p, 'x', NaN)")]
    [InlineData("let t = {x: 1}; let p = new Proxy(t, {has: function(o, k) { return o === t && k === 'other'; }, deleteProperty: function() { return false; }}); 'other' in p && !('x' in p) && !Reflect.deleteProperty(p, 'x') && t.x === 1")]
    [InlineData("let t = {}; let p = new Proxy(t, {set: function() { return 0; }, defineProperty: function() { return ''; }, setPrototypeOf: function() { return null; }, preventExtensions: function() { return undefined; }}); !Reflect.set(p, 'x', 1) && !Reflect.defineProperty(p, 'x', {value: 1}) && !Reflect.setPrototypeOf(p, null) && !Reflect.preventExtensions(p)")]
    [InlineData("let p = new Proxy({}, { getOwnPropertyDescriptor: function() { return {value: 7, configurable: true, enumerable: true}; }, ownKeys: function() { return {0: 'b', 1: '1', 2: '0', length: '3.5'}; }}); let k = Object.keys(p); let d = Object.getOwnPropertyDescriptor(p, 'b'); k.length === 3 && k[0] === 'b' && k[1] === '1' && k[2] === '0' && d.value === 7 && !d.writable")]
    [InlineData("let p = new Proxy({}, {ownKeys: function() { return {0: 'x', length: {valueOf: function() { return 1; }}}; }}); Reflect.ownKeys(p)[0] === 'x'")]
    [InlineData("let t = {a: 1, b: 2}; let p = new Proxy(t, {ownKeys: function() { return ['b', 'a']; }, preventExtensions: function(o) { return Reflect.preventExtensions(o); }, isExtensible: function(o) { return Reflect.isExtensible(o); }}); Reflect.preventExtensions(p) && !Object.isExtensible(p) && Reflect.ownKeys(p)[0] === 'b'")]
    [InlineData("let t = {}; let p = new Proxy(t, {defineProperty: function(o, k, d) { return d.value === 5 && d.hasOwnProperty('value') && !d.hasOwnProperty('writable') && Reflect.defineProperty(o, k, d); }}); Reflect.defineProperty(p, 'x', {value: 5, configurable: false}) && t.x === 5")]
    [InlineData("let t = {}; let h = {getPrototypeOf: function(o) { return Reflect.getPrototypeOf(o); }, setPrototypeOf: function(o, proto) { return Reflect.setPrototypeOf(o, proto); }}; let p = new Proxy(t, h); Reflect.setPrototypeOf(p, null) && Object.getPrototypeOf(p) === null && Object.getPrototypeOf(t) === null")]
    [InlineData("let p = new Proxy({}, {getPrototypeOf: function() { throw 'getPrototypeOf'; }}); let child = {}; Reflect.setPrototypeOf(child, p) && Object.getPrototypeOf(child) === p")]
    [InlineData("let t = {x: 1}; let r = Proxy.revocable(t, {}); let p = r.proxy; let revoke = r.revoke; p.x === 1 && revoke() === undefined && revoke() === undefined && typeof p === 'object' && t.x === 1")]
    [InlineData("let r; let h = {}; Object.defineProperty(h, 'get', {get: function() { r.revoke(); return function(t, k) { return t[k]; }; }}); r = Proxy.revocable({x: 1}, h); r.proxy.x === 1")]
    [InlineData("let p = new Proxy({x: 1}, {ownKeys: function() { return ['x', 'extra']; }, getOwnPropertyDescriptor: function(t, k) { return Reflect.getOwnPropertyDescriptor(t, k); }}); let keys = Object.keys(p); keys.length === 1 && keys[0] === 'x'")]
    public void SupportsProxyOperations(string script) =>
        Assert.True(new Engine().Evaluate(script).ToBoolean());

    [Theory]
    [InlineData("Proxy({}, {})")]
    [InlineData("new Proxy({}, null)")]
    [InlineData("new Proxy(1, {})")]
    [InlineData("new Proxy(function() {}, {})")]
    [InlineData("Proxy.revocable({}, undefined)")]
    [InlineData("let p = new Proxy({}, {get: 1}); p.x")]
    [InlineData("let p = new Proxy(Object.freeze({x: -0}), {get: function() { return 0; }}); p.x")]
    [InlineData("let p = new Proxy(Object.freeze({x: 1}), {set: function() { return true; }}); Reflect.set(p, 'x', 2)")]
    [InlineData("let t = {}; Object.defineProperty(t, 'x', {set: function() {}}); let p = new Proxy(t, {get: function() { return 1; }}); p.x")]
    [InlineData("let t = {}; Object.defineProperty(t, 'x', {get: function() {}}); let p = new Proxy(t, {set: function() { return true; }}); Reflect.set(p, 'x', 2)")]
    [InlineData("let p = new Proxy(Object.freeze({x: 1}), {has: function() { return false; }}); 'x' in p")]
    [InlineData("let p = new Proxy(Object.preventExtensions({x: 1}), {has: function() { return false; }}); 'x' in p")]
    [InlineData("let p = new Proxy(Object.preventExtensions({x: 1}), {deleteProperty: function() { return true; }}); Reflect.deleteProperty(p, 'x')")]
    [InlineData("let p = new Proxy(Object.seal({x: 1}), {deleteProperty: function() { return true; }}); delete p.x")]
    [InlineData("let p = new Proxy(Object.freeze({x: 1}), {getOwnPropertyDescriptor: function() { return undefined; }}); Reflect.getOwnPropertyDescriptor(p, 'x')")]
    [InlineData("let p = new Proxy(Object.freeze({x: 1}), {getOwnPropertyDescriptor: function() { return {value: 2}; }}); Reflect.getOwnPropertyDescriptor(p, 'x')")]
    [InlineData("let p = new Proxy({}, {getOwnPropertyDescriptor: function() { return null; }}); Reflect.getOwnPropertyDescriptor(p, 'x')")]
    [InlineData("let p = new Proxy({}, {getOwnPropertyDescriptor: function() { return {value: 1}; }}); Reflect.getOwnPropertyDescriptor(p, 'x')")]
    [InlineData("let p = new Proxy(Object.seal({x: 1}), {getOwnPropertyDescriptor: function() { return {value: 1, enumerable: true}; }}); Reflect.getOwnPropertyDescriptor(p, 'x')")]
    [InlineData("let p = new Proxy(Object.preventExtensions({}), {defineProperty: function() { return true; }}); Reflect.defineProperty(p, 'x', {configurable: true})")]
    [InlineData("let p = new Proxy({}, {defineProperty: function() { return true; }}); Reflect.defineProperty(p, 'x', {configurable: false})")]
    [InlineData("let p = new Proxy(Object.seal({x: 1}), {defineProperty: function() { return true; }}); Reflect.defineProperty(p, 'x', {writable: false})")]
    [InlineData("let p = new Proxy(Object.freeze({x: 1}), {defineProperty: function() { return true; }}); Reflect.defineProperty(p, 'x', {value: 2})")]
    [InlineData("let p = new Proxy({}, {ownKeys: function() { return 'x'; }}); Reflect.ownKeys(p)")]
    [InlineData("let p = new Proxy({}, {ownKeys: function() { return ['x', 'x']; }}); Reflect.ownKeys(p)")]
    [InlineData("let p = new Proxy({}, {ownKeys: function() { return [1]; }}); Reflect.ownKeys(p)")]
    [InlineData("let p = new Proxy({}, {ownKeys: function() { return [Symbol('x')]; }}); Reflect.ownKeys(p)")]
    [InlineData("let t = {}; Object.defineProperty(t, 'x', {}); let p = new Proxy(t, {ownKeys: function() { return []; }}); Reflect.ownKeys(p)")]
    [InlineData("let p = new Proxy(Object.preventExtensions({x: 1}), {ownKeys: function() { return []; }}); Reflect.ownKeys(p)")]
    [InlineData("let p = new Proxy(Object.preventExtensions({}), {ownKeys: function() { return ['x']; }}); Reflect.ownKeys(p)")]
    [InlineData("let p = new Proxy({}, {getPrototypeOf: function() { return 0; }}); Object.getPrototypeOf(p)")]
    [InlineData("let p = new Proxy(Object.preventExtensions({}), {getPrototypeOf: function() { return null; }}); Object.getPrototypeOf(p)")]
    [InlineData("let p = new Proxy(Object.preventExtensions({}), {setPrototypeOf: function() { return true; }}); Reflect.setPrototypeOf(p, null)")]
    [InlineData("let p = new Proxy({}, {isExtensible: function() { return false; }}); Object.isExtensible(p)")]
    [InlineData("let p = new Proxy({}, {preventExtensions: function() { return true; }}); Reflect.preventExtensions(p)")]
    public void RejectsInvalidArgumentsAndTrapResults(string script) =>
        Assert.Equal("TypeError", Assert.Throws<JsException>(() => new Engine().Evaluate(script)).ErrorName);

    [Theory]
    [InlineData("p.x")]
    [InlineData("p.x = 1")]
    [InlineData("'x' in p")]
    [InlineData("delete p.x")]
    [InlineData("Reflect.defineProperty(p, 'x', {})")]
    [InlineData("Reflect.getOwnPropertyDescriptor(p, 'x')")]
    [InlineData("Reflect.ownKeys(p)")]
    [InlineData("Object.getOwnPropertySymbols(p)")]
    [InlineData("Object.getPrototypeOf(p)")]
    [InlineData("Reflect.setPrototypeOf(p, null)")]
    [InlineData("Object.isExtensible(p)")]
    [InlineData("Reflect.preventExtensions(p)")]
    public void ThrowsForEveryRevokedInternalOperation(string operation)
    {
        var script = $"let result = Proxy.revocable({{}}, {{}}); let p = result.proxy; result.revoke(); {operation}";
        Assert.Equal("TypeError", Assert.Throws<JsException>(() => new Engine().Evaluate(script)).ErrorName);
    }
}
