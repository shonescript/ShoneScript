// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneJs.Tests;

public class TypedArrayBuiltinTests
{
    [Theory]
    [InlineData("let a = new Uint8Array([257, -1, 2.5]); a.length === 3 && a[0] === 1 && a[1] === 255 && a[2] === 2")]
    [InlineData("let a = new Int16Array([65535, 2]); a[0] === -1 && a.byteLength === 4 && Int16Array.BYTES_PER_ELEMENT === 2")]
    [InlineData("let a = new Uint16Array([-2.5, 65537]); a[0] === 65534 && a[1] === 1")]
    [InlineData("let a = new Uint32Array([-1, 4294967297, Infinity, NaN]); a[0] === 4294967295 && a[1] === 1 && a[2] === 0 && a[3] === 0")]
    [InlineData("let a = new Int32Array([2147483648, -2147483649]); a[0] === -2147483648 && a[1] === 2147483647")]
    [InlineData("let b = new ArrayBuffer(4); let a = new Uint8Array(b); a[1] = 42; a[1] === 42 && a.buffer === b && a.length === 4")]
    [InlineData("let a = new Uint8Array([1,2,3]); let b = a.toReversed(); b.join() === '3,2,1' && a.join() === '1,2,3'")]
    [InlineData("let a = new Uint8Array([1,2,3]); a.with(1,9).join() === '1,9,3' && a.slice(1).join() === '2,3' && a.at(-1) === 3")]
    [InlineData("let a = new Uint8Array([3,1,2]); a.map(function(x){return x*2}).join() === '6,2,4' && a.filter(function(x){return x>1}).join() === '3,2' && a.toSorted().join() === '1,2,3' && a.join() === '3,1,2'")]
    [InlineData("let a = new Uint8Array([1,2,3]); let b=a.subarray(1); b[0]=9; a.join() === '1,9,3' && a.fill(4,1).join() === '1,4,4' && a.copyWithin(0,1).join() === '4,4,4'")]
    [InlineData("let a = new Uint8Array([1,2]); a.every(function(x){return x>0}) && a.some(function(x){return x===2}) && a.find(function(x){return x===2})===2 && a.findIndex(function(x){return x===2})===1 && a.includes(1) && a.indexOf(2)===1")]
    public void SupportsTypedArrayBaselineAndCopyingMethods(string script) =>
        Assert.True(new ShoneJs.JsEngine().Evaluate(script).ToBoolean());
}
