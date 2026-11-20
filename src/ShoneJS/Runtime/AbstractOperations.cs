using System.Globalization;
using System.Numerics;

namespace ShoneJS.Runtime;

public static class AbstractOperations
{
    public static bool StrictEqual(JsValue left, JsValue right) => left.Type == right.Type && left.Type switch
    {
        JsType.Undefined or JsType.Null => true,
        JsType.Number => left.ToNumber() == right.ToNumber(),
        JsType.Object or JsType.Function or JsType.Symbol => ReferenceEquals(left.Data, right.Data),
        _ => Equals(left.Data, right.Data)
    };

    public static bool SameValue(JsValue left, JsValue right)
    {
        if (left.Type != JsType.Number || right.Type != JsType.Number) return StrictEqual(left, right);
        var x = left.ToNumber();
        var y = right.ToNumber();
        return double.IsNaN(x) ? double.IsNaN(y) : BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y);
    }

    public static bool LooseEqual(JsValue left, JsValue right)
    {
        if (left.Type == right.Type) return StrictEqual(left, right);
        if (IsNullish(left) || IsNullish(right)) return IsNullish(left) && IsNullish(right);
        if (left.Type == JsType.Boolean) return LooseEqual(JsValue.From(left.ToNumber()), right);
        if (right.Type == JsType.Boolean) return LooseEqual(left, JsValue.From(right.ToNumber()));
        if (left.Type == JsType.Number && right.Type == JsType.String || left.Type == JsType.String && right.Type == JsType.Number)
            return left.ToNumber() == right.ToNumber();
        if (left.Type == JsType.BigInt && right.Type is JsType.String or JsType.Number) return BigIntEquals((BigInteger)left.Data!, right);
        if (right.Type == JsType.BigInt && left.Type is JsType.String or JsType.Number) return BigIntEquals((BigInteger)right.Data!, left);
        if (left.Data is JsObject && right.Data is not JsObject) return LooseEqual(ToPrimitive(left), right);
        if (right.Data is JsObject && left.Data is not JsObject) return LooseEqual(left, ToPrimitive(right));
        return false;
    }

    internal static JsValue ToPrimitive(JsValue value, bool preferString = false)
    {
        if (value.Data is not JsObject obj) return value;
        string[] methods = preferString ? ["toString", "valueOf"] : ["valueOf", "toString"];
        foreach (var name in methods)
        {
            if (obj.Get(name, value).Data is not JsFunction function) continue;
            var result = function.Call(value, []);
            if (result.Data is not JsObject) return result;
        }
        throw JsException.TypeError("Cannot convert object to primitive value");
    }

    private static bool BigIntEquals(BigInteger integer, JsValue other)
    {
        if (other.Type == JsType.String) return TryParseBigInt((string)other.Data!, out var parsed) && parsed == integer;
        var number = other.ToNumber();
        return double.IsFinite(number) && Math.Truncate(number) == number && new BigInteger(number) == integer;
    }

    public static bool IsNullish(JsValue value) => value.Type is JsType.Null or JsType.Undefined;

    internal static bool TryParseBigInt(string text, out BigInteger value)
    {
        text = TrimNumericString(text);
        value = BigInteger.Zero;
        if (text.Length == 0) return true;
        if (text.Length >= 2 && text[0] == '0')
        {
            var radix = text[1] switch { 'x' or 'X' => 16, 'o' or 'O' => 8, 'b' or 'B' => 2, _ => 0 };
            if (radix != 0)
            {
                if (text.Length == 2) return false;
                foreach (var ch in text.AsSpan(2))
                {
                    var digit = ch switch { >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10, >= 'A' and <= 'F' => ch - 'A' + 10, _ => -1 };
                    if (digit < 0 || digit >= radix) return false;
                    value = value * radix + digit;
                }
                return true;
            }
        }
        var digits = text[0] is '+' or '-' ? text.AsSpan(1) : text.AsSpan();
        if (digits.IsEmpty) return false;
        foreach (var ch in digits)
            if (ch is < '0' or > '9') return false;
        return BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    internal static double StringToNumber(string text)
    {
        text = TrimNumericString(text);
        if (text.Length == 0) return 0;
        if (text is "Infinity" or "+Infinity") return double.PositiveInfinity;
        if (text == "-Infinity") return double.NegativeInfinity;
        if (text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
            return TryParseBigInt(text, out var integer) ? (double)integer : double.NaN;
        foreach (var ch in text)
            if (ch is not (>= '0' and <= '9' or '+' or '-' or '.' or 'E' or 'e')) return double.NaN;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }

    private static string TrimNumericString(string text)
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
        var text = value.ToString("G", CultureInfo.InvariantCulture);
        var exponentIndex = text.IndexOf('E');
        if (exponentIndex < 0) return text;
        var exponent = int.Parse(text.AsSpan(exponentIndex + 1), CultureInfo.InvariantCulture);
        if (exponent is < -6 or >= 21)
            return text[..exponentIndex] + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);

        var negative = text[0] == '-';
        var mantissa = text[(negative ? 1 : 0)..exponentIndex].Replace(".", "", StringComparison.Ordinal);
        var decimalIndex = exponent + 1;
        var expanded = decimalIndex <= 0 ? "0." + new string('0', -decimalIndex) + mantissa
            : decimalIndex >= mantissa.Length ? mantissa + new string('0', decimalIndex - mantissa.Length)
            : mantissa.Insert(decimalIndex, ".");
        return negative ? "-" + expanded : expanded;
    }
}
