// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneJs.Runtime;

/// <summary>An abrupt completion caused by a JavaScript throw.</summary>
public sealed class JsException : InvalidOperationException
{
    public JsValue Value { get; }
    public string? ErrorName => (Value.Data as JsError)?.Name;

    public JsException(JsValue value) : base(value.Data is JsError error ? error.Message : value.ToString()) => Value = value;

    internal static JsException TypeError(string message) => new(JsValue.From(new JsError("TypeError", message)));
    internal static JsException ReferenceError(string message) => new(JsValue.From(new JsError("ReferenceError", message)));
    internal static JsException RangeError(string message) => new(JsValue.From(new JsError("RangeError", message)));
}

public sealed class JsError : JsObject
{
    public string Name { get; }
    public string Message { get; }

    public JsError(string name, string message)
    {
        Name = name;
        Message = message;
        Set("name", JsValue.From(name));
        Set("message", JsValue.From(message));
    }
}
