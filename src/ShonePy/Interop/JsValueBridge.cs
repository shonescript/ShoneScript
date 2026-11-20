// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShoneJS.Runtime;

namespace ShonePy.Interop;

/// <summary>Explicitly converts scalar values between the Python and JavaScript hosts.</summary>
public static class JsValueBridge
{
    public static JsValue ToJavaScript(PyValue value) => value.Type switch
    {
        PyType.None => JsValue.Null,
        PyType.Boolean => JsValue.From((bool)value.Data!),
        PyType.Integer => JsValue.From((BigInt)value.Data!),
        PyType.Float => JsValue.From((double)value.Data!),
        PyType.String => JsValue.From((string)value.Data!),
        _ => throw new NotSupportedException($"Cannot transfer Python {value.Type} to JavaScript.")
    };

    public static PyValue ToPython(JsValue value) => value.Type switch
    {
        JsType.Null => PyValue.None,
        JsType.Boolean => PyValue.From((bool)value.Data!),
        JsType.BigInt => PyValue.From((BigInt)value.Data!),
        JsType.Number => PyValue.From((double)value.Data!),
        JsType.String => PyValue.From((string)value.Data!),
        _ => throw new NotSupportedException($"Cannot transfer JavaScript {value.Type} to Python.")
    };
}
