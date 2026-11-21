// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using Sdcb.Arithmetic;

namespace ShoneJs.Runtime;

/// <summary>Shared ECMAScript-style coercion and comparison operations.</summary>
public static class AbstractOperations
{
    /// <summary>Implements === semantics: NaN is unequal to itself and signed zeroes compare equal.</summary>
    public static bool StrictEqual(JsValue left, JsValue right) => left.Type == right.Type && left.Type switch
    {
        JsType.Undefined or JsType.Null => true,
        JsType.Number => left.ToNumber() == right.ToNumber(),
        JsType.Boolean => left.ToBoolean() == right.ToBoolean(),
        JsType.Object or JsType.Function or JsType.Symbol => ReferenceEquals(left.Data, right.Data),
        _ => Equals(left.Data, right.Data)
    };

    /// <summary>Implements SameValue semantics, distinguishing signed zero while equating NaN values.</summary>
    public static bool SameValue(JsValue left, JsValue right)
    {
        if (left.Type != JsType.Number || right.Type != JsType.Number) return StrictEqual(left, right);
        var x = left.ToNumber();
        var y = right.ToNumber();
        return double.IsNaN(x) ? double.IsNaN(y) : BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y);
    }

    /// <summary>Implements the supported abstract-equality coercions, including nullish and BigInt cases.</summary>
    public static bool LooseEqual(JsValue left, JsValue right)
    {
        if (left.Type == right.Type) return StrictEqual(left, right);
        if (IsNullish(left) || IsNullish(right)) return IsNullish(left) && IsNullish(right);
        if (left.Type == JsType.Boolean) return LooseEqual(JsValue.From(left.ToNumber()), right);
        if (right.Type == JsType.Boolean) return LooseEqual(left, JsValue.From(right.ToNumber()));
        if (left.Type == JsType.Number && right.Type == JsType.String || left.Type == JsType.String && right.Type == JsType.Number)
            return left.ToNumber() == right.ToNumber();
        if (left.Type == JsType.BigInt && right.Type is JsType.String or JsType.Number) return BigIntEquals((BigInt)left.Data!, right);
        if (right.Type == JsType.BigInt && left.Type is JsType.String or JsType.Number) return BigIntEquals((BigInt)right.Data!, left);
        if (left.ObjectValue is null)
            return right.ObjectValue is not null && LooseEqual(left, ToPrimitive(right));
        if (right.ObjectValue is null) return LooseEqual(ToPrimitive(left), right);
        return false;
    }

    internal static JsValue ToPrimitive(JsValue value, bool preferString = false)
    {
        if (value.ObjectValue is not { } obj) return value;
        var first = preferString ? "toString" : "valueOf";
        var second = preferString ? "valueOf" : "toString";
        return TryCall(first) ?? TryCall(second) ?? throw JsException.TypeError("Cannot convert object to primitive value");

        JsValue? TryCall(string name)
        {
            if (obj.Get(name, value).ObjectValue is not JsFunction function) return null;
            var result = function.Call(value, []);
            return result.ObjectValue is null ? result : null;
        }
    }

    private static bool BigIntEquals(BigInt integer, JsValue other)
    {
        if (other.Type == JsType.String) return TryParseBigInt((string)other.Data!, out var parsed) && parsed == integer;
        var number = other.ToNumber();
        return double.IsFinite(number) && Math.Truncate(number) == number && new BigInt(number) == integer;
    }

    public static bool IsNullish(JsValue value) => value.Type is JsType.Null or JsType.Undefined;

    internal static bool TryParseBigInt(string text, out BigInt value) => TryParseBigInt(TrimNumericString(text), out value);

    private static bool TryParseBigInt(ReadOnlySpan<char> text, out BigInt value)
    {
        value = BigInt.Zero;
        if (text.Length == 0) return true;
        if (text.Length >= 2 && text[0] == '0')
        {
            var radix = text[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0)
            {
                if (text.Length == 2) return false;
                foreach (var ch in text[2..])
                {
                    var digit = ch switch { >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10, >= 'A' and <= 'F' => ch - 'A' + 10, _ => -1 };
                    if (digit < 0 || digit >= radix) return false;
                    value = value * radix + digit;
                }
                return true;
            }
        }
        var digits = text[0] is '+' or '-' ? text[1..] : text;
        if (digits.IsEmpty) return false;
        foreach (var ch in digits)
            if (ch is < '0' or > '9') return false;
        return BigInt.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    internal static double StringToNumber(string source)
    {
        var text = TrimNumericString(source);
        if (text.Length == 0) return 0;
        if (text is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (text is "-Infinity") return double.NegativeInfinity;
        if (text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
            return TryParseBigInt(text, out var integer) ? (double)integer : double.NaN;
        foreach (var ch in text)
            if (ch is not (>= '0' and <= '9' or '+' or '-' or '.' or 'E' or 'e')) return double.NaN;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }

    private static ReadOnlySpan<char> TrimNumericString(ReadOnlySpan<char> text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsWhiteSpace(text[start])) start++;
        while (start < end && IsWhiteSpace(text[end - 1])) end--;
        return text[start..end];
    }

    private static bool IsWhiteSpace(char ch) => ch is '\t' or '\v' or '\f' or '\r' or '\n' or '\u2028' or '\u2029' or '\ufeff'
        || char.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator;

    internal static string NumberToString(double value)
    {
        if (value == 0) return "0";
        // Invariant round-trip format uses at most 17 significant digits and a three-digit exponent.
        Span<char> buffer = stackalloc char[32];
        if (!value.TryFormat(buffer, out var length, "R", CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Number formatting exceeded the buffer");
        var text = buffer[..length];
        var exponentIndex = text.IndexOf('E');
        if (exponentIndex < 0) return new string(text);
        var exponent = int.Parse(text[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        if (exponent is < -6 or >= 21)
        {
            text[exponentIndex] = 'e';
            var start = exponentIndex + 2; // Keep the sign and skip exponent padding.
            var digits = text[start..].TrimStart('0');
            digits.CopyTo(text[start..]);
            return new string(text[..(start + digits.Length)]);
        }

        // JavaScript uses fixed notation for exponents in [-6, 21).
        Span<char> result = stackalloc char[32];
        var signLength = text[0] == '-' ? 1 : 0;
        if (signLength != 0) result[0] = '-';
        var mantissa = text[signLength..exponentIndex];
        if (mantissa.Length > 1)
        {
            mantissa[2..].CopyTo(mantissa[1..]);
            mantissa = mantissa[..^1];
        }

        var output = result[signLength..];
        var decimalIndex = exponent + 1;
        if (decimalIndex <= 0)
        {
            output[0] = '0';
            output[1] = '.';
            output.Slice(2, -decimalIndex).Fill('0');
            mantissa.CopyTo(output[(2 - decimalIndex)..]);
            length = 2 - decimalIndex + mantissa.Length;
        }
        else if (decimalIndex >= mantissa.Length)
        {
            mantissa.CopyTo(output);
            output[mantissa.Length..decimalIndex].Fill('0');
            length = decimalIndex;
        }
        else
        {
            mantissa[..decimalIndex].CopyTo(output);
            output[decimalIndex] = '.';
            mantissa[decimalIndex..].CopyTo(output[(decimalIndex + 1)..]);
            length = mantissa.Length + 1;
        }
        return new string(result[..(signLength + length)]);
    }
}
