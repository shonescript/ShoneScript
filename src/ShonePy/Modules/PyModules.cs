// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using Sdcb.Arithmetic;

namespace ShonePy.Modules;

internal static class PyModules
{
    public static PyDict Import(string name) => name switch
    {
        "math" => MathModule(),
        "json" => JsonModule(),
        "re" => PyRegexModule.CreateModule(),
        "functools" => PyFunctoolsModule.CreateModule(),
        "itertools" => PyItertoolsModule.CreateModule(),
        "collections" => PyCollectionsModule.CreateModule(),
        "datetime" => PyDateTimeModule.CreateModule(),
        "typing" => PyTypingModule.CreateModule(),
        "sys" => SysModule(),
        _ => throw new PythonException("ModuleNotFoundError", $"No module named '{name}'")
    };

    private static PyDict MathModule()
    {
        var module = new PyDict();
        Put("pi", Math.PI); Put("e", Math.E); Put("tau", Math.Tau); Put("inf", double.PositiveInfinity); Put("nan", double.NaN);
        Function("sqrt", 1, a => CheckedUnary(a[0], Math.Sqrt));
        Function("sin", 1, a => CheckedUnary(a[0], Math.Sin));
        Function("cos", 1, a => CheckedUnary(a[0], Math.Cos));
        Function("tan", 1, a => CheckedUnary(a[0], Math.Tan));
        Function("floor", 1, a => IntegralResult(a[0], Math.Floor));
        Function("ceil", 1, a => IntegralResult(a[0], Math.Ceiling));
        Function("trunc", 1, a => IntegralResult(a[0], Math.Truncate));
        Function("fabs", 1, a => Math.Abs(Number(a[0])));
        Function("log", 1, a => CheckedUnary(a[0], Math.Log, rejectNonPositive: true));
        Function("exp", 1, a => CheckedUnary(a[0], Math.Exp));
        Function("pow", 2, a => Math.Pow(Number(a[0]), Number(a[1])));
        Function("isfinite", 1, a => double.IsFinite(Number(a[0])));
        Function("isnan", 1, a => double.IsNaN(Number(a[0])));
        Function("isqrt", 1, a => IntegerSquareRoot(MathInteger(a[0])));
        Function("factorial", 1, a => Factorial(MathInteger(a[0])));
        Function("comb", 2, a => CountSelections(MathInteger(a[0]), MathInteger(a[1]), ordered: false));
        Put("perm", new PyCallable(a =>
        {
            if (a.Count is < 1 or > 2) throw new PythonException("TypeError", "perm() takes one or two arguments");
            BigInt size = MathInteger(a[0]);
            return PyValue.From(a.Count == 1 || a[1].Type == PyType.None
                ? Factorial(size)
                : CountSelections(size, MathInteger(a[1]), ordered: true));
        }, "perm"));
        Put("gcd", new PyCallable(a => FoldIntegers(a, lcm: false), "gcd"));
        Put("lcm", new PyCallable(a => FoldIntegers(a, lcm: true), "lcm"));
        return module;
        void Put(string key, object value) => module[PyValue.From(key)] = PyValue.From(value);
        void Function(string key, int count, Func<IReadOnlyList<PyValue>, object> call) => Put(key, PyValue.From(new PyCallable(a => { Arity(key, count, a); return PyValue.From(call(a)); }, key)));
    }

    private static BigInt MathInteger(PyValue value) => value.Type switch
    {
        PyType.Integer => (BigInt)value.Data!,
        PyType.Boolean => value.IsTruthy() ? BigInt.One : BigInt.Zero,
        _ => throw new PythonException("TypeError", $"'{PyOperations.TypeName(value)}' object cannot be interpreted as an integer")
    };

    private static PyValue FoldIntegers(IReadOnlyList<PyValue> arguments, bool lcm)
    {
        BigInt result = lcm ? BigInt.One : BigInt.Zero;
        foreach (var argument in arguments)
        {
            PyIterator.ChargeStep?.Invoke(0);
            BigInt value = BigInt.Abs(MathInteger(argument));
            if (lcm)
            {
                result = result.IsZero || value.IsZero ? BigInt.Zero : result / GreatestCommonDivisor(result, value) * value;
                CheckMathSize(result);
            }
            else result = GreatestCommonDivisor(result, value);
        }
        return PyValue.From(result);
    }

    private static BigInt GreatestCommonDivisor(BigInt left, BigInt right)
    {
        while (!right.IsZero)
        {
            PyIterator.ChargeStep?.Invoke(0);
            (left, right) = (right, left % right);
        }
        return left;
    }

    private static BigInt IntegerSquareRoot(BigInt value)
    {
        if (value.Sign < 0) throw new PythonException("ValueError", "isqrt() argument must be nonnegative");
        if (value <= BigInt.One) return value;
        BigInt estimate = BigInt.One << checked((int)((value.GetBitLength() + 1) / 2));
        while (true)
        {
            PyIterator.ChargeStep?.Invoke(0);
            BigInt next = (estimate + value / estimate) >> 1;
            if (next >= estimate) return estimate;
            estimate = next;
        }
    }

    private static BigInt Factorial(BigInt value)
    {
        if (value.Sign < 0) throw new PythonException("ValueError", "factorial() not defined for negative values");
        // Half the factors are at least two, so this bound rejects oversized inputs cheaply.
        if (value > new BigInt(16_000_000)) throw MathSizeError();
        BigInt result = BigInt.One;
        for (int factor = 2, end = (int)value; factor <= end; factor++)
        {
            PyIterator.ChargeStep?.Invoke(0);
            result *= new BigInt(factor);
            CheckMathSize(result);
        }
        return result;
    }

    private static BigInt CountSelections(BigInt size, BigInt count, bool ordered)
    {
        string name = ordered ? "perm" : "comb";
        if (size.Sign < 0) throw new PythonException("ValueError", $"n must be a non-negative integer for {name}()");
        if (count.Sign < 0) throw new PythonException("ValueError", $"k must be a non-negative integer for {name}()");
        if (count > size) return BigInt.Zero;
        if (!ordered && count > size - count) count = size - count;

        BigInt result = BigInt.One;
        BigInt start = size - count;
        for (BigInt i = BigInt.One; i <= count; i += BigInt.One)
        {
            PyIterator.ChargeStep?.Invoke(0);
            result *= start + i;
            if (!ordered) result /= i;
            CheckMathSize(result);
        }
        return result;
    }

    private static void CheckMathSize(BigInt result)
    {
        if (result.GetBitLength() > 8_000_000) throw MathSizeError();
    }

    private static PythonException MathSizeError() => new("OverflowError", "integer result exceeds the interpreter result limit");

    private static PyDict JsonModule()
    {
        var module = new PyDict();
        module[PyValue.From("dumps")] = PyValue.From(new PyCallable(a =>
        {
            Arity("dumps", 1, a);
            return PyValue.From(Serialize(a[0]));
        }, "dumps"));
        module[PyValue.From("loads")] = PyValue.From(new PyCallable(a =>
        {
            Arity("loads", 1, a);
            try
            {
                using var document = JsonDocument.Parse(String(a[0]));
                return FromElement(document.RootElement);
            }
            catch (JsonException ex) { throw new PythonException("JSONDecodeError", ex.Message); }
        }, "loads"));
        return module;
    }

    private static PyDict SysModule()
    {
        var module = new PyDict();
        module[PyValue.From("version")] = PyValue.From("ShonePy (Python 3.15 target, partial implementation)");
        string platform = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsAndroid() ? "android"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsFreeBSD() ? $"freebsd{Environment.OSVersion.Version.Major}"
            : Environment.OSVersion.Platform.ToString().ToLowerInvariant();
        module[PyValue.From("platform")] = PyValue.From(platform);
        module[PyValue.From("maxsize")] = PyValue.From((long)nint.MaxValue);
        return module;
    }

    private static double CheckedUnary(PyValue value, Func<double, double> operation, bool rejectNonPositive = false)
    {
        double input = Number(value);
        if (rejectNonPositive && input <= 0)
            throw new PythonException("ValueError", "math domain error");
        double result = operation(input);
        if (double.IsNaN(result) && !double.IsNaN(input))
            throw new PythonException("ValueError", "math domain error");
        if (double.IsInfinity(result) && double.IsFinite(input))
            throw new PythonException("OverflowError", "math range error");
        return result;
    }

    private static PyValue IntegralResult(PyValue value, Func<double, double> operation)
    {
        if (value.Type == PyType.Integer) return value;
        double number = Number(value);
        if (double.IsNaN(number)) throw new PythonException("ValueError", "cannot convert float NaN to integer");
        if (double.IsInfinity(number)) throw new PythonException("OverflowError", "cannot convert float infinity to integer");
        return PyValue.From(new BigInt(operation(number)));
    }

    private static double Number(PyValue value)
    {
        if (value.Type == PyType.Float) return (double)value.Data!;
        if (value.Type == PyType.Boolean) return value.IsTruthy() ? 1 : 0;
        if (value.Type != PyType.Integer) throw new PythonException("TypeError", "must be a real number");
        double result = double.Parse(value.ToPythonString(), CultureInfo.InvariantCulture);
        if (!double.IsFinite(result)) throw new PythonException("OverflowError", "int too large to convert to float");
        return result;
    }
    private static string String(PyValue value) => value.Type == PyType.String ? (string)value.Data! : throw new PythonException("TypeError", "the JSON object must be str");
    private static void Arity(string name, int count, IReadOnlyList<PyValue> args)
    {
        if (args.Count != count) throw new PythonException("TypeError", $"{name}() takes exactly {count} argument(s) ({args.Count} given)");
    }
    private static string Serialize(PyValue value)
    {
        if (value.Type == PyType.Integer) return value.ToPythonString();
        if (value.Type == PyType.List) return "[" + string.Join(",", ((PyList)value.Data!).Select(Serialize)) + "]";
        if (value.Type == PyType.Tuple) return "[" + string.Join(",", ((PyTuple)value.Data!).Select(Serialize)) + "]";
        if (value.Type == PyType.Dictionary)
        {
            var entries = ((PyDict)value.Data!).Select(pair => JsonSerializer.Serialize(String(pair.Key)) + ":" + Serialize(pair.Value));
            return "{" + string.Join(",", entries) + "}";
        }
        return JsonSerializer.Serialize<object?>(value.Type switch
        {
            PyType.None => null, PyType.Boolean => value.IsTruthy(), PyType.Float => (double)value.Data!,
            PyType.String => (string)value.Data!,
            _ => throw new PythonException("TypeError", $"Object of type {value.Type} is not JSON serializable")
        });
    }
    private static PyValue FromElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => PyValue.None, JsonValueKind.True => PyValue.True, JsonValueKind.False => PyValue.False,
        JsonValueKind.String => PyValue.From(value.GetString()!), JsonValueKind.Number when value.TryGetInt64(out long integer) => PyValue.From(integer),
        JsonValueKind.Number => ParseNumber(value), JsonValueKind.Array => PyValue.From(new PyList(value.EnumerateArray().Select(FromElement))),
        JsonValueKind.Object => ObjectDict(value), _ => PyValue.None
    };
    private static PyValue ParseNumber(JsonElement element)
    {
        string text = element.GetRawText();
        return text.AsSpan().IndexOfAny('.', 'e', 'E') < 0
            ? PyValue.From(BigInt.Parse(text))
            : PyValue.From(element.GetDouble());
    }

    private static PyValue ObjectDict(JsonElement element)
    {
        var result = new PyDict();
        foreach (var item in element.EnumerateObject()) result[PyValue.From(item.Name)] = FromElement(item.Value);
        return PyValue.From(result);
    }
}
