// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneScript;
using ShoneScript.Runtime;
using ShonePy.Interop;

namespace ShonePy.Tests;

public sealed class PythonInteropTests
{
    [Fact]
    public void TransfersLargeIntegersWithoutRounding()
    {
        var python = new PythonEngine();
        var js = new Engine();
        js.SetGlobal("value", JsValueBridge.ToJavaScript(python.Evaluate("2 ** 80")));
        python.SetGlobal("value", JsValueBridge.ToPython(js.Evaluate("value + 1n")));
        Assert.Equal("1208925819614629174706177", python.Evaluate("value").ToString());
    }

    [Fact]
    public void TransfersScalarsAndDistinguishesMissingValues()
    {
        Assert.Equal(JsType.Null, JsValueBridge.ToJavaScript(PyValue.None).Type);
        Assert.Equal(PyType.None, JsValueBridge.ToPython(JsValue.Null).Type);
        Assert.Equal(PyType.Float, JsValueBridge.ToPython(JsValue.From(42d)).Type);
        Assert.Equal(true, JsValueBridge.ToJavaScript(PyValue.True).Data);
        Assert.Equal("text", JsValueBridge.ToPython(JsValue.From("text")).Data);
        Assert.Throws<NotSupportedException>(() => JsValueBridge.ToPython(JsValue.Undefined));
    }
}
