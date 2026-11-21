// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class WeakBuiltinsTests
{
    [Theory]
    [InlineData("let k = {}; let m = new WeakMap(); m.set(k, 7) === m && m.has(k) && m.get(k) === 7 && m.delete(k) && !m.has(k) && m.get(k) === undefined")]
    [InlineData("let k = {}; let s = new WeakSet(); s.add(k) === s && s.has(k) && s.delete(k) && !s.has(k)")]
    [InlineData("let k = {}; let r = new WeakRef(k); r.deref() === k && r.deref() === k")]
    [InlineData("typeof WeakMap === 'function' && WeakMap.name === 'WeakMap' && WeakMap.length === 0 && typeof WeakSet === 'function' && typeof WeakRef === 'function'")]
    [InlineData("let k = Symbol('private'); let m = new WeakMap(); m.set(k, 3); m.get(k) === 3 && m.has(k)")]
    [InlineData("let k = Symbol.for('shared'); let m = new WeakMap(); let ok = false; try { m.set(k, 3); } catch (e) { ok = true; } ok")]
    [InlineData("let k = Symbol.for('shared-ref'); let ok = false; try { new WeakRef(k); } catch (e) { ok = true; } ok")]
    public void SupportsWeakBuiltins(string script) => Assert.True(new JsEngine().Evaluate(script).ToBoolean());

    [Theory]
    [InlineData("new WeakMap().set(1, 2)")]
    [InlineData("new WeakMap().set(Symbol.for('registered'), 2)")]
    [InlineData("new WeakSet().add(null)")]
    [InlineData("new WeakSet().add(Symbol.for('registered'))")]
    [InlineData("new WeakRef(1)")]
    [InlineData("WeakMap()")]
    [InlineData("WeakSet()")]
    [InlineData("WeakRef({})")]
    [InlineData("WeakMap.prototype.has.call({}, {})")]
    public void RejectsInvalidWeakBuiltinUsage(string script) =>
        Assert.Equal("TypeError", Assert.Throws<JsException>(() => new JsEngine().Evaluate(script)).ErrorName);
}
