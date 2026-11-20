// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Text;
using Sdcb.Arithmetic;
using ShoneJs.Runtime;
using ShonePy.Modules;

namespace ShonePy;

public static class PyOperations
{
    public static bool IsTruthy(PyValue value) => value.IsTruthy();
    public static bool Equal(PyValue a, PyValue b)
    {
        if (a.Data is PyDateTimeModule.Value temporal) return temporal.Equals(b.Data as PyDateTimeModule.Value);
        if (IsNumericOrBoolean(a) && IsNumericOrBoolean(b))
        {
            if (IsNaN(a) || IsNaN(b)) return false;
            return CompareNumbers(a, b) == 0;
        }
        if (a.Type != b.Type) return false;
        return a.Type switch
        {
            PyType.BigFloat => (BigFloat)a.Data! == (BigFloat)b.Data!,
            PyType.None => true,
            PyType.Boolean => (bool)a.Data! == (bool)b.Data!,
            PyType.String => (string)a.Data! == (string)b.Data!,
            PyType.Bytes => ((PyBytes)a.Data!).Equals((PyBytes)b.Data!),
            PyType.List => ((PyList)a.Data!).SequenceEqual((PyList)b.Data!),
            PyType.Tuple => ((PyTuple)a.Data!).SequenceEqual((PyTuple)b.Data!),
            PyType.Dictionary => DictEqual((PyDict)a.Data!, (PyDict)b.Data!),
            PyType.Set => ((PySet)a.Data!).SetEquals((PySet)b.Data!),
            PyType.Range => ((PyRange)a.Data!).Equals((PyRange)b.Data!),
            _ => ReferenceEquals(a.Data, b.Data)
        };
    }
    public static int Hash(PyValue value)
    {
        if (value.Type != PyType.Tuple) EnsureHashable(value);
        if (value.Data is PyDateTimeModule.Value temporal) return temporal.GetHashCode();
        return value.Type switch
        {
            PyType.None => 0, PyType.Boolean => ((bool)value.Data! ? BigInt.One : BigInt.Zero).GetHashCode(),
            PyType.Integer => ((BigInt)value.Data!).GetHashCode(), PyType.Float => FloatHash((double)value.Data!),
            PyType.String => StringComparer.Ordinal.GetHashCode((string)value.Data!),
            PyType.Bytes => ((PyBytes)value.Data!).GetHashCode(),
            PyType.Range => ((PyRange)value.Data!).GetHashCode(),
            PyType.Class or PyType.Instance or PyType.Descriptor or PyType.Super or PyType.Coroutine => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Data!),
            PyType.Tuple => ((PyTuple)value.Data!).Aggregate(17, (h, v) => HashCode.Combine(h, Hash(v))), _ => 0
        };
    }
    public static void EnsureHashable(PyValue value)
    {
        if (value.Type is PyType.List or PyType.Dictionary or PyType.Set or PyType.Callable or PyType.BigFloat)
            throw new PythonException("TypeError", $"unhashable type: '{TypeName(value)}'");
        if (value.Data is PyTuple tuple)
            foreach (var item in tuple) EnsureHashable(item);
    }
    public static PyValue Unary(string op, PyValue value) => op switch
    {
        "not" => PyValue.From(!value.IsTruthy()), "+" when IsNumber(value) || value.Type == PyType.BigFloat => value,
        "-" when value.Type == PyType.BigFloat => PyValue.From(-(BigFloat)value.Data!),
        "-" when value.Type == PyType.Integer => PyValue.From(-(BigInt)value.Data!),
        "-" when IsNumber(value) => PyValue.From(-Number(value)),
        "~" when value.Type == PyType.Integer => PyValue.From(~(BigInt)value.Data!),
        _ when value.Data is PyDateTimeModule.Value temporal && PyDateTimeModule.Unary(op, temporal) is { } result => result,
        _ => throw new PythonException("TypeError", $"bad operand type for unary {op}: '{TypeName(value)}'")
    };
    public static PyValue Binary(PyValue a, string op, PyValue b)
    {
        if ((a.Type == PyType.Temporal || b.Type == PyType.Temporal) && PyDateTimeModule.Binary(a, op, b) is { } temporal)
            return temporal;
        if (a.Data is PySet leftSet && b.Data is PySet rightSet && PySet.IsSetOperator(op))
            return PyValue.From(leftSet.Operate(op, rightSet));
        if (op == "+" && a.Data is PyBytes leftBytes && b.Data is PyBytes rightBytes)
            return PyValue.From(leftBytes.Concat(rightBytes));
        if (op == "*" && a.Data is PyBytes bytes && IsInteger(b)) return PyValue.From(bytes.Repeat(Integer(b)));
        if (op == "*" && b.Data is PyBytes right && IsInteger(a)) return PyValue.From(right.Repeat(Integer(a)));
        if (op == "+" && a.Type == PyType.String && b.Type == PyType.String) return PyValue.From((string)a.Data! + (string)b.Data!);
        if (op == "+" && a.Type == PyType.List && b.Type == PyType.List) return PyValue.From(new PyList(((PyList)a.Data!).Concat((PyList)b.Data!)));
        if (op == "+" && a.Type == PyType.Tuple && b.Type == PyType.Tuple) return PyValue.From(new PyTuple(((PyTuple)a.Data!).Concat((PyTuple)b.Data!)));
        if (op == "*" && (a.Type == PyType.String || a.Type == PyType.List || a.Type == PyType.Tuple) && IsInteger(b))
            return Repeat(a, Integer(b));
        if (op == "*" && (b.Type == PyType.String || b.Type == PyType.List || b.Type == PyType.Tuple) && IsInteger(a))
            return Repeat(b, Integer(a));
        if (a.Data is BigFloat leftBigFloat && b.Data is BigFloat rightBigFloat && op is "+" or "-" or "*" or "/")
        {
            EnsureBigFloatMagnitude(leftBigFloat);
            EnsureBigFloatMagnitude(rightBigFloat);
            int precision = Math.Max(leftBigFloat.Precision, rightBigFloat.Precision);
            if (op == "/" && rightBigFloat.IsZero)
                throw new PythonException("ZeroDivisionError", "BigFloat division by zero");

            BigFloat result = op switch
            {
                "+" => BigFloat.Add(leftBigFloat, rightBigFloat, precision, BigFloatSupport.Rounding),
                "-" => BigFloat.Subtract(leftBigFloat, rightBigFloat, precision, BigFloatSupport.Rounding),
                "*" => BigFloat.Multiply(leftBigFloat, rightBigFloat, precision, BigFloatSupport.Rounding),
                _ => BigFloat.Divide(leftBigFloat, rightBigFloat, precision, BigFloatSupport.Rounding)
            };
            EnsureBigFloatMagnitude(result);
            return PyValue.From(result);
        }
        if (!IsNumericOrBoolean(a) || !IsNumericOrBoolean(b)) throw new PythonException("TypeError", $"unsupported operand type(s) for {op}: '{TypeName(a)}' and '{TypeName(b)}'");
        var ints = IsInteger(a) && IsInteger(b);
        if (ints)
        {
            var x = Integer(a); var y = Integer(b);
            return op switch
            {
                "+" => PyValue.From(x + y), "-" => PyValue.From(x - y), "*" => PyValue.From(x * y),
                "&" => PyValue.From(x & y), "|" => PyValue.From(x | y), "^" => PyValue.From(x ^ y),
                "<<" when y >= BigInt.Zero && y <= new BigInt(1000000) => PyValue.From(x << (int)y),
                ">>" when y >= BigInt.Zero && y <= new BigInt(1000000) => PyValue.From(x >> (int)y),
                "//" when y != BigInt.Zero => PyValue.From(FloorDiv(x, y)), "%" when y != BigInt.Zero => PyValue.From(x - FloorDiv(x, y) * y),
                "**" => Power(x, y), "/" when y != BigInt.Zero => PyValue.From((double)x / (double)y),
                _ => throw new PythonException("ZeroDivisionError", "integer division or modulo by zero")
            };
        }
        var l = Number(a); var r = Number(b);
        return op switch { "+" => PyValue.From(l+r), "-" => PyValue.From(l-r), "*" => PyValue.From(l*r), "/" => r == 0 ? throw new PythonException("ZeroDivisionError", "division by zero") : PyValue.From(l/r), "//" => PyValue.From(Math.Floor(l/r)), "%" => PyValue.From(l-Math.Floor(l/r)*r), "**" => PyValue.From(Math.Pow(l,r)), _ => throw new PythonException("TypeError", $"unsupported operator {op}") };
    }
    private static void EnsureBigFloatMagnitude(BigFloat value)
    {
        if (!BigFloatSupport.IsMagnitudeSupported(value))
            throw new PythonException("OverflowError", "BigFloat magnitude exceeds the interpreter arithmetic limit");
    }

    public static bool Compare(PyValue a, string op, PyValue b)
    {
        if (op == "==") return Equal(a, b);
        if (op == "!=") return !Equal(a, b);
        if (op == "is") return Identity(a, b);
        if (op == "is not") return !Identity(a, b);
        if (a.Data is PySet leftSet && b.Data is PySet rightSet)
            return op switch
            {
                "<" => leftSet.Count < rightSet.Count && leftSet.IsSubsetOf(rightSet),
                "<=" => leftSet.IsSubsetOf(rightSet),
                ">" => leftSet.Count > rightSet.Count && rightSet.IsSubsetOf(leftSet),
                ">=" => rightSet.IsSubsetOf(leftSet),
                _ => throw new PythonException("SyntaxError", $"unknown comparison {op}")
            };
        if (a.Data is BigFloat leftBigFloat && b.Data is BigFloat rightBigFloat)
            return op switch
            {
                "<" => leftBigFloat < rightBigFloat,
                "<=" => leftBigFloat <= rightBigFloat,
                ">" => leftBigFloat > rightBigFloat,
                ">=" => leftBigFloat >= rightBigFloat,
                _ => throw new PythonException("SyntaxError", $"unknown comparison {op}")
            };
        int cmp;
        if (IsNumericOrBoolean(a) && IsNumericOrBoolean(b))
        {
            if (IsNaN(a) || IsNaN(b)) return false;
            cmp = CompareNumbers(a, b);
        }
        else if (a.Data is PyBytes leftBytes && b.Data is PyBytes rightBytes) cmp = leftBytes.CompareTo(rightBytes);
        else if (a.Type == PyType.String && b.Type == PyType.String) cmp = string.CompareOrdinal((string)a.Data!, (string)b.Data!);
        else if (a.Data is PyDateTimeModule.Value left && b.Data is PyDateTimeModule.Value right && left.Type == right.Type)
            cmp = left.Microseconds.CompareTo(right.Microseconds);
        else throw new PythonException("TypeError", $"'{op}' not supported between instances of '{TypeName(a)}' and '{TypeName(b)}'");
        return op switch { "<" => cmp < 0, "<=" => cmp <= 0, ">" => cmp > 0, ">=" => cmp >= 0, _ => throw new PythonException("SyntaxError", $"unknown comparison {op}") };
    }
    public static bool Identity(PyValue a, PyValue b) => a.Type == b.Type && (a.Type switch
    {
        PyType.None => true,
        PyType.Boolean => (bool)a.Data! == (bool)b.Data!,
        PyType.Integer => (BigInt)a.Data! == (BigInt)b.Data!,
        PyType.Float => (double)a.Data! == (double)b.Data!,
        _ => ReferenceEquals(a.Data, b.Data)
    });
    public static bool Contains(PyValue container, PyValue item) => container.Type switch
    {
        PyType.String when item.Type == PyType.String => ((string)container.Data!).Contains((string)item.Data!, StringComparison.Ordinal),
        PyType.Bytes => item.Data is PyBytes needle ? ((PyBytes)container.Data!).Contains(needle) : ((PyBytes)container.Data!).Contains(ByteArgument(item)),
        PyType.List => ((PyList)container.Data!).Any(value => Equal(value, item)),
        PyType.Tuple => ((PyTuple)container.Data!).Any(value => Equal(value, item)),
        PyType.Dictionary => ((PyDict)container.Data!).ContainsKey(item),
        PyType.Set => ((PySet)container.Data!).Contains(item),
        PyType.Range => ((PyRange)container.Data!).Contains(item),
        PyType.Iterator => Iterate(container).Any(value => Equal(value, item)),
        _ => throw new PythonException("TypeError", $"argument of type '{TypeName(container)}' is not iterable")
    };
    public static PyValue GetItem(PyValue target, PyValue index)
    {
        if (target.Type == PyType.Dictionary) return ((PyDict)target.Data!)[index];
        if (target.Data is PyRange range) return range.GetItem(index);
        if (target.Type == PyType.Set) throw new PythonException("TypeError", "'set' object is not subscriptable");
        if (target.Data is PyBytes bytes)
        {
            if (!IsInteger(index)) throw new PythonException("TypeError", "byte indices must be integers or slices");
            var offset = Integer(index);
            if (offset.Sign < 0) offset += bytes.Count;
            if (offset.Sign < 0 || offset >= new BigInt(bytes.Count))
                throw new PythonException("IndexError", "index out of range");
            return PyValue.From((int)bytes[(int)offset]);
        }
        var i = Index(index);
        if (target.Type == PyType.List) return ((PyList)target.Data!)[Normalize(i, ((PyList)target.Data!).Count)];
        if (target.Type == PyType.Tuple) return ((PyTuple)target.Data!)[Normalize(i, ((PyTuple)target.Data!).Count)];
        if (target.Type == PyType.String)
        {
            var runes = ((string)target.Data!).EnumerateRunes().ToArray();
            return PyValue.From(runes[Normalize(i, runes.Length)].ToString());
        }
        throw new PythonException("TypeError", $"'{TypeName(target)}' object is not subscriptable");
    }
    internal readonly record struct Slice(PyValue? Start, PyValue? Stop, PyValue? Step);

    public static PyValue GetSlice(PyValue target, PyValue? start, PyValue? stop, PyValue? step) =>
        GetSlice(target, new Slice(start, stop, step));

    internal static PyValue GetSlice(PyValue target, Slice slice)
    {
        if (target.Data is PyRange range)
        {
            var (start, stop, step) = NormalizeSliceBounds(range.Length, slice);
            return PyValue.From(range.GetSlice(start, stop, step));
        }

        if (target.Data is PyBytes bytes)
        {
            var bounds = NormalizeSlice(bytes.Count, slice);
            return PyValue.From(bytes.Slice(bounds.First, bounds.Stride, bounds.Count));
        }

        PyValue[] items = target.Type switch
        {
            PyType.List => ((PyList)target.Data!).ToArray(),
            PyType.Tuple => ((PyTuple)target.Data!).ToArray(),
            PyType.String => ((string)target.Data!).EnumerateRunes().Select(r => PyValue.From(r.ToString())).ToArray(),
            _ => throw new PythonException("TypeError", $"'{TypeName(target)}' object is not subscriptable")
        };
        var (first, stride, count) = NormalizeSlice(items.Length, slice);
        var result = new List<PyValue>(count);
        for (int i = 0; i < count; i++)
            result.Add(items[first + i * stride]);
        return target.Type switch
        {
            PyType.List => PyValue.From(new PyList(result)),
            PyType.Tuple => PyValue.From(new PyTuple(result)),
            _ => PyValue.From(string.Concat(result.Select(x => (string)x.Data!)))
        };
    }

    internal static void SetSlice(PyValue target, Slice slice, PyValue value, Action charge)
    {
        if (target.Data is not PyList list)
            throw new PythonException("TypeError", $"'{TypeName(target)}' object does not support item assignment");

        int originalLength = list.Count;
        var (first, stride, count) = NormalizeSlice(originalLength, slice);
        // Materialize before mutating so self-assignment and iterators over the list see the original contents.
        var replacement = new List<PyValue>();
        foreach (var item in Iterate(value))
        {
            charge();
            replacement.Add(item);
        }

        // Iterator callbacks can resize the target during materialization.
        if (list.Count != originalLength)
            (first, stride, count) = NormalizeSlice(list.Count, slice);

        if (stride == 1)
        {
            list.ReplaceRange(first, count, replacement);
            return;
        }

        if (replacement.Count != count)
            throw new PythonException("ValueError", $"attempt to assign sequence of size {replacement.Count} to extended slice of size {count}");
        for (int i = 0; i < count; i++)
            list[first + i * stride] = replacement[i];
    }

    private static (int First, int Stride, int Count) NormalizeSlice(int length, Slice slice)
    {
        var (start, stop, step) = NormalizeSliceBounds(new BigInt(length), slice);
        int first = (int)start;
        int end = (int)stop;
        BigInt limit = new(int.MaxValue);
        int stride = step > limit ? int.MaxValue : step < -limit ? -int.MaxValue : (int)step;
        int distance = stride < 0 ? first - end : end - first;
        int count = distance <= 0 ? 0 : 1 + (distance - 1) / Math.Abs(stride);
        return (first, stride, count);
    }

    private static (BigInt Start, BigInt Stop, BigInt Step) NormalizeSliceBounds(BigInt length, Slice slice)
    {
        BigInt step = slice.Step is { Type: not PyType.None } value ? SliceInteger(value) : BigInt.One;
        if (step.IsZero) throw new PythonException("ValueError", "slice step cannot be zero");
        bool backwards = step.Sign < 0;
        var lower = backwards ? -BigInt.One : BigInt.Zero;
        var upper = backwards ? length - BigInt.One : length;
        var start = SliceBound(slice.Start, length, lower, upper, backwards ? upper : lower);
        var stop = SliceBound(slice.Stop, length, lower, upper, backwards ? lower : upper);
        return (start, stop, step);
    }

    private static BigInt SliceBound(PyValue? value, BigInt length, BigInt lower, BigInt upper, BigInt defaultBound)
    {
        if (value is null or { Type: PyType.None }) return defaultBound;
        BigInt bound = SliceInteger(value.Value);
        if (bound.Sign < 0) bound += length;
        return bound < lower ? lower : bound > upper ? upper : bound;
    }

    private static BigInt SliceInteger(PyValue value) => IsInteger(value)
        ? Integer(value)
        : throw new PythonException("TypeError", "slice indices must be integers or None or have an __index__ method");

    public static void SetItem(PyValue target, PyValue index, PyValue value)
    {
        if (target.Type == PyType.Dictionary) { ((PyDict)target.Data!)[index] = value; return; }
        if (target.Type == PyType.List) { var list=(PyList)target.Data!; list[Normalize(Index(index),list.Count)] = value; return; }
        throw new PythonException("TypeError", $"'{TypeName(target)}' object does not support item assignment");
    }
    public static IEnumerable<PyValue> Iterate(PyValue value) => value.Type switch
    {
        PyType.List => (PyList)value.Data!, PyType.Tuple => (PyTuple)value.Data!,
        PyType.Dictionary => ((PyDict)value.Data!).Select(x => x.Key),
        PyType.Set => (PySet)value.Data!,
        PyType.String => IterateString((string)value.Data!),
        PyType.Bytes => IterateBytes((PyBytes)value.Data!),
        PyType.Range => (PyRange)value.Data!,
        PyType.Iterator => (PyIterator)value.Data!,
        _ => throw new PythonException("TypeError", $"'{TypeName(value)}' object is not iterable")
    };

    internal static PyIterator GetIterator(PyValue value) => value.Type switch
    {
        PyType.Iterator => (PyIterator)value.Data!,
        PyType.Range => ((PyRange)value.Data!).GetIterator(),
        PyType.Bytes => GetBytesIterator((PyBytes)value.Data!),
        PyType.List => PyIterator.FromEnumerable("list_iterator", IterateList((PyList)value.Data!)),
        PyType.Tuple => PyIterator.FromEnumerable("tuple_iterator", Iterate(value)),
        PyType.Dictionary => PyIterator.FromEnumerable("dict_keyiterator", Iterate(value)),
        PyType.Set => PyIterator.FromEnumerable("set_iterator", Iterate(value)),
        _ => PyIterator.FromEnumerable("str_iterator", Iterate(value))
    };

    private static IEnumerable<PyValue> IterateBytes(PyBytes bytes)
    {
        foreach (var item in GetBytesIterator(bytes)) yield return item;
    }

    private static PyIterator GetBytesIterator(PyBytes bytes)
    {
        int index = 0;
        return new PyIterator("bytes_iterator", () => index < bytes.Count ? PyValue.From((int)bytes[index++]) : (PyValue?)null);
    }

    private static IEnumerable<PyValue> IterateList(PyList list)
    {
        for (int index = 0; index < list.Count; index++) yield return list[index];
    }

    private static IEnumerable<PyValue> IterateString(string text)
    {
        foreach (Rune rune in text.EnumerateRunes()) yield return PyValue.From(rune.ToString());
    }
    private static PyValue Repeat(PyValue value, BigInt count)
    {
        if (count <= BigInt.Zero) return value.Type switch
        {
            PyType.String => PyValue.From(string.Empty),
            PyType.List => PyValue.From(new PyList()),
            _ => PyValue.From(new PyTuple())
        };
        var n = count > new BigInt(1000000) ? 1000000 : (int)count;
        var length = value.Type == PyType.String ? ((string)value.Data!).Length : value.Type == PyType.List ? ((PyList)value.Data!).Count : ((PyTuple)value.Data!).Count;
        if ((long)length * n > 8_000_000) throw new PythonException("OverflowError", "repeated sequence is too large");
        if (value.Type == PyType.String) return PyValue.From(string.Concat(Enumerable.Repeat((string)value.Data!,n)));
        if (value.Type == PyType.List) return PyValue.From(new PyList(Enumerable.Repeat((PyList)value.Data!,n).SelectMany(x=>x)));
        return PyValue.From(new PyTuple(Enumerable.Repeat((PyTuple)value.Data!,n).SelectMany(x=>x)));
    }
    private static bool DictEqual(PyDict a, PyDict b)
    {
        if (a is PyCounter && b is PyCounter)
            return a.All(pair => Equal(pair.Value, b[pair.Key])) && b.All(pair => Equal(pair.Value, a[pair.Key]));
        return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out PyValue value) && Equal(pair.Value, value));
    }
    private static PyValue Power(BigInt value, BigInt exponent)
    {
        if (exponent < BigInt.Zero)
        {
            if (value.IsZero) throw new PythonException("ZeroDivisionError", "zero cannot be raised to a negative power");
            return PyValue.From(Math.Pow((double)value, (double)exponent));
        }
        if (exponent.IsZero) return PyValue.From(BigInt.One);
        if (value.IsZero || value == BigInt.One) return PyValue.From(value);
        if (value == -BigInt.One) return PyValue.From((exponent & BigInt.One).IsZero ? BigInt.One : -BigInt.One);
        if (exponent > new BigInt(8_000_000 / Math.Max(1L, value.GetBitLength())))
            throw new PythonException("OverflowError", "integer power exceeds the interpreter result limit");
        var remaining = (int)exponent;
        var result = BigInt.One;
        while (remaining > 0)
        {
            if ((remaining & 1) != 0) result *= value;
            remaining >>= 1;
            if (remaining > 0) value *= value;
        }
        return PyValue.From(result);
    }

    private static BigInt FloorDiv(BigInt x, BigInt y) { var q=x/y; var r=x%y; return r != BigInt.Zero && ((r < BigInt.Zero) != (y < BigInt.Zero)) ? q-1 : q; }
    private static int Index(PyValue value) => value.Type == PyType.Integer ? (int)(BigInt)value.Data! : throw new PythonException("TypeError", "sequence indices must be integers");
    private static int Normalize(int index,int length) { if (index < 0) index += length; if (index < 0 || index >= length) throw new PythonException("IndexError", "index out of range"); return index; }
    private static int FloatHash(double value) => double.IsFinite(value) && value == Math.Truncate(value)
        ? new BigInt(value).GetHashCode()
        : value.GetHashCode();
    internal static byte ByteArgument(PyValue value)
    {
        if (!IsInteger(value))
            throw new PythonException("TypeError", $"'{TypeName(value)}' object cannot be interpreted as an integer");
        var integer = Integer(value);
        if (integer.Sign < 0 || integer > new BigInt((int)byte.MaxValue))
            throw new PythonException("ValueError", "bytes must be in range(0, 256)");
        return (byte)(int)integer;
    }
    private static bool IsNumber(PyValue v) => v.Type is PyType.Integer or PyType.Float;
    private static bool IsInteger(PyValue v) => v.Type is PyType.Integer or PyType.Boolean;
    private static bool IsNumericOrBoolean(PyValue v) => IsNumber(v) || v.Type == PyType.Boolean;
    private static bool IsNaN(PyValue v) => v.Type == PyType.Float && double.IsNaN((double)v.Data!);
    private static BigInt Integer(PyValue v) => v.Type == PyType.Boolean ? ((bool)v.Data! ? BigInt.One : BigInt.Zero) : (BigInt)v.Data!;
    private static int CompareNumbers(PyValue a, PyValue b)
    {
        if (!IsInteger(a) && !IsInteger(b)) return ((double)a.Data!).CompareTo((double)b.Data!);
        if (IsInteger(a) && IsInteger(b)) return Integer(a).CompareTo(Integer(b));
        var integer = IsInteger(a) ? Integer(a) : Integer(b);
        var number = (double)(IsInteger(a) ? b.Data! : a.Data!);
        var comparison = CompareIntegerToDouble(integer, number);
        return IsInteger(a) ? comparison : -comparison;
    }
    private static int CompareIntegerToDouble(BigInt integer, double number)
    {
        if (double.IsPositiveInfinity(number)) return -1;
        if (double.IsNegativeInfinity(number)) return 1;
        var truncated = new BigInt(number);
        var comparison = integer.CompareTo(truncated);
        if (comparison != 0) return comparison;
        var fraction = number - (double)truncated;
        return fraction > 0 ? -1 : fraction < 0 ? 1 : 0;
    }
    private static double Number(PyValue v) => v.Type switch { PyType.Integer => (double)(BigInt)v.Data!, PyType.Boolean => (bool)v.Data! ? 1 : 0, _ => (double)v.Data! };
    public static string TypeName(PyValue value) => value.Data is PyDateTimeModule.Value temporal ? temporal.Type.ToString() : value.Type switch { PyType.Super=>"super", PyType.Coroutine=>"coroutine", PyType.None=>"NoneType", PyType.Boolean=>"bool", PyType.Integer=>"int", PyType.Float=>"float", PyType.BigFloat=>"BigFloat", PyType.String=>"str", PyType.Bytes=>"bytes", PyType.List=>"list", PyType.Tuple=>"tuple", PyType.Range=>"range", PyType.Set=>"set", PyType.Dictionary when value.Data is PyCounter=>"Counter", PyType.Dictionary=>"dict", PyType.Iterator=>((PyIterator)value.Data!).Name, PyType.ExceptionClass or PyType.Class=>"type", PyType.Descriptor=>((Runtime.PyDescriptor)value.Data!).TypeName, PyType.Instance=>((Runtime.PyInstance)value.Data!).Class.Name, PyType.Exception=>((Runtime.PyExceptionInstance)value.Data!).Type.Name, _=>"function" };
}
