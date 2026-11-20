// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using Sdcb.Arithmetic;
using ShonePy.Modules;
using ShonePy.Runtime;

namespace ShonePy;

public enum PyType { None, Boolean, Integer, Float, String, List, Tuple, Dictionary, Callable, Iterator, ExceptionClass, Exception, Temporal, Range, Set, Bytes, Class, Instance, Descriptor, Super, Coroutine, BigFloat }

/// <summary>A Python value in the supported first-milestone runtime.</summary>
public readonly struct PyValue : IEquatable<PyValue>
{
    public PyType Type { get; }
    public object? Data { get; }
    public PyType Kind => Type;
    public object? Payload => Data;
    private PyValue(PyType type, object? data) { Type = type; Data = data; }

    public static PyValue None => new(PyType.None, null);
    public static PyValue True => new(PyType.Boolean, true);
    public static PyValue False => new(PyType.Boolean, false);
    public static PyValue From(bool value) => value ? True : False;
    public static PyValue From(BigInt value) => new(PyType.Integer, value);
    public static PyValue From(int value) => From(new BigInt(value));
    public static PyValue From(long value) => From(new BigInt(value));
    public static PyValue From(double value) => new(PyType.Float, value);
    public static PyValue From(BigFloat value) => new(PyType.BigFloat, value);
    public static PyValue From(string value) => new(PyType.String, value);
    public static PyValue From(PyBytes value) => new(PyType.Bytes, value);
    public static PyValue From(PyList value) => new(PyType.List, value);
    public static PyValue From(PyTuple value) => new(PyType.Tuple, value);
    public static PyValue From(PyDict value) => new(PyType.Dictionary, value);
    public static PyValue From(PyRange value) => new(PyType.Range, value);
    public static PyValue From(PySet value) => new(PyType.Set, value);
    public static PyValue From(PyCallable value) => new(PyType.Callable, value);
    internal static PyValue From(PyClass value) => new(PyType.Class, value);
    internal static PyValue From(PyInstance value) => new(PyType.Instance, value);
    internal static PyValue From(PyDescriptor value) => new(PyType.Descriptor, value);
    internal static PyValue From(PySuper value) => new(PyType.Super, value);
    internal static PyValue From(PyIterator value) => new(PyType.Iterator, value);
    internal static PyValue From(PyCoroutine value) => new(PyType.Coroutine, value);
    internal static PyValue From(PyExceptionClass value) => new(PyType.ExceptionClass, value);
    internal static PyValue From(PyExceptionInstance value) => new(PyType.Exception, value);
    internal static PyValue From(PyDateTimeModule.Value value) => new(PyType.Temporal, value);
    public static PyValue From(object? value) => value switch
    {
        null => None, PyValue py => py, bool b => From(b), BigInt n => From(n), int n => From(n), long n => From(n),
        double n => From(n), float n => From(n), BigFloat n => From(n), string s => From(s), PyList l => From(l), PyTuple t => From(t), PyDict d => From(d), PyCallable c => From(c), PyIterator iterator => From(iterator), PyCoroutine coroutine => From(coroutine),
        PyDateTimeModule.Value temporal => From(temporal), PyRange range => From(range), PySet set => From(set), PyBytes bytes => From(bytes),
        PyClass cls => From(cls), PyInstance instance => From(instance), PyDescriptor descriptor => From(descriptor), PySuper super => From(super),
        _ => throw new ArgumentException($"Unsupported Python value: {value.GetType().Name}")
    };

    public bool IsTruthy() => Type switch
    {
        PyType.None => false, PyType.Boolean => (bool)Data!, PyType.Integer => (BigInt)Data! != BigInt.Zero,
        PyType.Float => (double)Data! != 0, PyType.BigFloat => !((BigFloat)Data!).IsZero,
        PyType.String => ((string)Data!).Length != 0,
        PyType.List => ((PyList)Data!).Count != 0, PyType.Tuple => ((PyTuple)Data!).Count != 0,
        PyType.Dictionary => ((PyDict)Data!).Count != 0, PyType.Range => !((PyRange)Data!).Length.IsZero,
        PyType.Set => ((PySet)Data!).Count != 0, PyType.Bytes => ((PyBytes)Data!).Count != 0,
        PyType.Temporal => ((PyDateTimeModule.Value)Data!).IsTruthy,
        _ => true
    };
    public bool ToBoolean() => IsTruthy();
    public PyValue Call(IReadOnlyList<PyValue> arguments) => Call(arguments, null);
    internal PyValue Call(IReadOnlyList<PyValue> arguments, IReadOnlyDictionary<string, PyValue>? keywords) => Data switch
    {
        PyCallable callable when keywords is { Count: > 0 } => callable.Invoke(arguments, keywords),
        PyCallable callable => callable.Invoke(arguments),
        PyClass cls => cls.Construct(arguments, keywords),
        PyMethodDescriptor { BindsClass: false } descriptor => descriptor.Function.Call(arguments, keywords),
        PyExceptionClass cls when keywords is { Count: > 0 } => throw new PythonException("TypeError", $"{cls.Name}() takes no keyword arguments"),
        PyExceptionClass cls => cls.Create(arguments),
        _ => throw new PythonException("TypeError", $"'{PyOperations.TypeName(this)}' object is not callable")
    };
    public string ToPythonString() => Type switch
    {
        PyType.None => "None", PyType.Boolean => (bool)Data! ? "True" : "False", PyType.Integer => Data!.ToString()!,
        PyType.Float => FormatFloat((double)Data!), PyType.BigFloat => PyBigFloatModule.Format((BigFloat)Data!),
        PyType.String => (string)Data!,
        _ => Data?.ToString() ?? "None"
    };
    private static string FormatFloat(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        string text = value.ToString("G", CultureInfo.InvariantCulture).ToLowerInvariant();
        // Python switches to scientific notation at a decimal exponent of 16.
        if (!text.Contains('e') && Math.Abs(value) >= 1e16)
        {
            string sign = value < 0 ? "-" : "";
            string digits = text[sign.Length..];
            int exponent = digits.Length - 1;
            digits = digits.TrimEnd('0');
            string significand = digits.Length == 1 ? digits : digits.Insert(1, ".");
            return sign + significand + "e+" + exponent.ToString(CultureInfo.InvariantCulture);
        }
        return text.Contains('.') || text.Contains('e') ? text : text + ".0";
    }

    public string Repr() => Data switch
    {
        string text => "'" + text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n") + "'",
        PyExceptionInstance error => error.Repr(),
        PyDateTimeModule.Value temporal => temporal.Repr(),
        _ => ToPythonString()
    };
    public override string ToString() => ToPythonString();
    public bool Equals(PyValue other) => PyOperations.Equal(this, other);
    public override bool Equals(object? obj) => obj is PyValue value && Equals(value);
    public override int GetHashCode() => PyOperations.Hash(this);
    public static implicit operator PyValue(bool value) => From(value);
    public static implicit operator PyValue(int value) => From(value);
    public static implicit operator PyValue(long value) => From(value);
    public static implicit operator PyValue(double value) => From(value);
    public static implicit operator PyValue(string value) => From(value);
}
