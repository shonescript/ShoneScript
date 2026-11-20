// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShoneJs.Runtime;

namespace ShonePy.Modules;

internal static class PyBigFloatModule
{
    internal static PyDict CreateModule() => new()
    {
        ["BigFloat"] = Function("BigFloat", 1, 2, Construct),
        ["pi"] = Function("pi", 0, 1, args => PyValue.From(BigFloat.Pi(
            args.Count == 0 ? BigFloatSupport.DefaultPrecision : Precision(args[0]), BigFloatSupport.Rounding)))
    };

    internal static PyValue GetAttribute(BigFloat value, string name) => name switch
    {
        "precision" => PyValue.From(value.Precision),
        "with_precision" => Function(name, 1, 1, args => PyValue.From(value.WithPrecision(Precision(args[0]), BigFloatSupport.Rounding))),
        "sqrt" => Function(name, 0, 0, _ => PyValue.From(BigFloat.Sqrt(value, value.Precision, BigFloatSupport.Rounding))),
        "to_float" => Function(name, 0, 0, _ => PyValue.From(value.ToDouble(BigFloatSupport.Rounding))),
        _ => throw new PythonException("AttributeError", $"'BigFloat' object has no attribute '{name}'")
    };

    internal static string Format(BigFloat value)
    {
        try { return BigFloatSupport.Format(value); }
        catch (ArgumentOutOfRangeException error) { throw new PythonException("OverflowError", error.Message); }
    }

    private static PyValue Construct(IReadOnlyList<PyValue> arguments)
    {
        var value = arguments[0];
        int precision = arguments.Count == 2 ? Precision(arguments[1])
            : value.Data is BigFloat number ? number.Precision : BigFloatSupport.DefaultPrecision;
        return PyValue.From(value.Data switch
        {
            string text => Parse(text, precision),
            BigInt integer => BigFloat.FromBigInt(integer, precision, BigFloatSupport.Rounding),
            double floating => BigFloat.FromDouble(floating, precision, BigFloatSupport.Rounding),
            BigFloat bigFloat => bigFloat.WithPrecision(precision, BigFloatSupport.Rounding),
            _ => throw new PythonException("TypeError", "BigFloat() requires a str, int, float or BigFloat")
        });
    }

    private static BigFloat Parse(string text, int precision)
    {
        try { return BigFloatSupport.Parse(text, precision); }
        catch (FormatException error) { throw new PythonException("ValueError", error.Message); }
        catch (ArgumentOutOfRangeException error) { throw new PythonException("OverflowError", error.Message); }
    }

    private static int Precision(PyValue value)
    {
        if (value.Data is not BigInt bits)
            throw new PythonException("TypeError", "BigFloat precision must be an integer");
        if (bits < BigFloatSupport.MinPrecision || bits > BigFloatSupport.MaxPrecision)
            throw new PythonException("ValueError", $"BigFloat precision must be between {BigFloatSupport.MinPrecision} and {BigFloatSupport.MaxPrecision} bits");
        return (int)bits;
    }

    private static PyValue Function(string name, int min, int max, Func<IReadOnlyList<PyValue>, PyValue> invoke) =>
        PyValue.From(new PyCallable(args =>
        {
            if (args.Count < min || args.Count > max)
            {
                string count = min == max ? $"exactly {min}" : $"between {min} and {max}";
                throw new PythonException("TypeError", $"{name}() takes {count} argument(s) ({args.Count} given)");
            }
            return invoke(args);
        }, name));
}
