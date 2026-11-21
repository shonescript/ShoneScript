// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;

namespace ShoneJs.Runtime.Builtins;

internal sealed class PrimitiveBuiltins
{
    private readonly JsObject _numberPrototype;
    private readonly JsObject _stringPrototype;

    internal PrimitiveBuiltins(Action<string, JsValue> setGlobal, JsObject objectPrototype, JsArray arrayPrototype, RegExpBuiltins regex)
    {
        _numberPrototype = new NumberObject(0, objectPrototype);
        InstallConstructor("Number", 1, _numberPrototype, (_, args) => JsValue.From(ConvertNumber(args)),
            args => JsValue.From(new NumberObject(ConvertNumber(args), _numberPrototype)));
        InstallMethod(_numberPrototype, "toFixed", 1, ToFixed);
        InstallMethod(_numberPrototype, "valueOf", 0, (receiver, _) => JsValue.From(RequireNumber(receiver)));
        InstallMethod(_numberPrototype, "toString", 0, (receiver, _) => JsValue.From(AbstractOperations.NumberToString(RequireNumber(receiver))));

        _stringPrototype = new JsObject { Prototype = objectPrototype };
        InstallConstructor("String", 1, _stringPrototype, (_, args) =>
        {
            if (args.Length == 0) return JsValue.From("");
            var input = args[0];
            return JsValue.From(input.Type == JsType.Symbol ? input.ToString()
                : AbstractOperations.ToPrimitive(input, preferString: true).ToJsString());
        });
        InstallMethod(_stringPrototype, "indexOf", 1, StringIndexOf);
        InstallMethod(_stringPrototype, "replace", 2, RegExpBuiltins.StringReplace);
        InstallMethod(_stringPrototype, "match", 1, regex.StringMatch);
        InstallMethod(_stringPrototype, "split", 2, (receiver, args) => StringSplit(receiver, args, arrayPrototype));

        var math = new JsObject { Prototype = objectPrototype };
        InstallMethod(math, "round", 1, (_, args) => JsValue.From(Round(ToNumber(At(args, 0)))));
        setGlobal("Math", JsValue.From(math));

        var datePrototype = new JsObject { Prototype = objectPrototype };
        InstallConstructor("Date", 7, datePrototype, (_, _) => JsValue.From(DateTime.Now.ToString(
            "ddd MMM dd yyyy HH:mm:ss 'GMT'zzzz", CultureInfo.InvariantCulture)),
            args => JsValue.From(new DateObject(ReadDate(args), datePrototype)));
        InstallMethod(datePrototype, "toISOString", 0, (receiver, _) =>
            JsValue.From(FormatIsoDate(RequireDate(receiver))));
        InstallMethod(datePrototype, "valueOf", 0, (receiver, _) => JsValue.From(RequireDate(receiver)));

        void InstallConstructor(string name, int arity, JsObject prototype, Func<JsValue, JsValue[], JsValue> call,
            Func<JsValue[], JsValue>? construct = null)
        {
            var function = CreateFunction(name, arity, call, construct);
            function.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(prototype)));
            prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(function), writable: true, configurable: true));
            setGlobal(name, JsValue.From(function));
        }

        void InstallMethod(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call) =>
            target.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From(CreateFunction(name, arity, call)), writable: true, configurable: true));

        JsFunction CreateFunction(string name, int arity, Func<JsValue, JsValue[], JsValue> call, Func<JsValue[], JsValue>? construct = null)
        {
            var function = new JsFunction(call, arity) { Prototype = objectPrototype, NativeConstruct = construct };
            function.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            return function;
        }
    }

    internal JsValue GetProperty(JsValue receiver, string key)
    {
        if (receiver.Type == JsType.Number) return _numberPrototype.Get(key, receiver);
        if (receiver.Data is not string text) return JsValue.Undefined;
        if (key == "length") return JsValue.From((double)text.Length);
        if (uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < text.Length
            && key == index.ToString(CultureInfo.InvariantCulture))
            return JsValue.From(text[(int)index].ToString());
        return _stringPrototype.Get(key, receiver);
    }

    private static JsValue StringIndexOf(JsValue receiver, JsValue[] args)
    {
        if (AbstractOperations.IsNullish(receiver)) throw JsException.TypeError("String.prototype.indexOf requires a receiver");
        var text = AbstractOperations.ToPrimitive(receiver, preferString: true).ToJsString();
        var search = AbstractOperations.ToPrimitive(At(args, 0), preferString: true).ToJsString();
        var number = ToNumber(At(args, 1));
        var position = double.IsNaN(number) ? 0 : (int)Math.Clamp(Math.Truncate(number), 0, text.Length);
        return JsValue.From((double)text.IndexOf(search, position, StringComparison.Ordinal));
    }

    private static JsValue StringSplit(JsValue receiver, JsValue[] args, JsArray arrayPrototype)
    {
        if (AbstractOperations.IsNullish(receiver)) throw JsException.TypeError("String.prototype.split requires a receiver");
        var text = AbstractOperations.ToPrimitive(receiver, preferString: true).ToJsString();
        var separator = At(args, 0);
        if (RegExpBuiltins.IsRegExpInstance(separator)) throw JsException.TypeError("Splitting with RegExp is not supported");
        var limitValue = At(args, 1);
        var limit = (double)uint.MaxValue;
        if (limitValue.Type != JsType.Undefined)
        {
            var numeric = ToNumber(limitValue);
            limit = double.IsFinite(numeric) ? Math.Truncate(numeric) % 4294967296d : 0;
            if (limit < 0) limit += 4294967296d;
        }
        var result = new JsArray { Prototype = arrayPrototype };
        if (limit == 0) return JsValue.From(result);
        if (separator.Type == JsType.Undefined) result.Add(JsValue.From(text));
        else
        {
            var delimiter = AbstractOperations.ToPrimitive(separator, preferString: true).ToJsString();
            if (text.Length > 1_048_576 || delimiter.Length > 1_048_576)
                throw JsException.RangeError("String split input limit exceeded");
            Interpreter.ChargeNativeWork(text.Length + delimiter.Length + 1L);
            var start = 0;
            while (result.Length < limit && (delimiter.Length > 0 || start < text.Length))
            {
                if (result.Length >= 32_768) throw JsException.RangeError("String split result limit exceeded");
                Interpreter.ChargeNativeWork(1);
                var end = delimiter.Length == 0 ? start + 1 : text.IndexOf(delimiter, start, StringComparison.Ordinal);
                result.Add(JsValue.From(end < 0 ? text[start..] : text[start..end]));
                if (end < 0) break;
                start = end + delimiter.Length;
            }
        }
        return JsValue.From(result);
    }

    private static JsValue At(JsValue[] args, int i) => i < args.Length ? args[i] : JsValue.Undefined;
    private static double ToNumber(JsValue value) => AbstractOperations.ToPrimitive(value).ToNumber();

    private static double Round(double number)
    {
        if (!double.IsFinite(number) || number == 0) return number;
        if (number is >= -0.5 and < 0) return -0d;
        var floor = Math.Floor(number);
        return number - floor >= 0.5 ? floor + 1 : floor;
    }

    private static double ConvertNumber(JsValue[] args)
    {
        if (args.Length == 0) return 0;
        var input = AbstractOperations.ToPrimitive(args[0]);
        return input.Data is Sdcb.Arithmetic.BigInt integer ? (double)integer : input.ToNumber();
    }

    private static double RequireNumber(JsValue receiver) => receiver.Data switch
    {
        double number => number,
        NumberObject wrapper => wrapper.Value,
        _ => throw JsException.TypeError("Number method requires a Number receiver")
    };

    private static JsValue ToFixed(JsValue receiver, JsValue[] args)
    {
        var number = RequireNumber(receiver);
        var fraction = ToNumber(At(args, 0));
        fraction = double.IsNaN(fraction) ? 0 : Math.Truncate(fraction);
        if (fraction is < 0 or > 100) throw JsException.RangeError("toFixed digits must be between 0 and 100");
        if (!double.IsFinite(number) || Math.Abs(number) >= 1e21)
            return JsValue.From(AbstractOperations.NumberToString(number));

        // Use the exact binary value so decimal rounding does not round twice.
        var digits = (int)fraction;
        var bits = BitConverter.DoubleToUInt64Bits(Math.Abs(number));
        var exponent = (int)(bits >> 52);
        var significand = bits & ((1UL << 52) - 1);
        if (exponent != 0) significand |= 1UL << 52;
        var shift = Math.Max(exponent, 1) - 1075;
        var scaled = significand * BigInteger.Pow(10, digits);
        // Positive magnitudes round up on ties; apply the original sign afterward.
        var rounded = shift >= 0 ? scaled << shift : (scaled + (BigInteger.One << (-shift - 1))) >> -shift;
        var text = rounded.ToString(CultureInfo.InvariantCulture);
        if (digits != 0)
        {
            text = text.PadLeft(digits + 1, '0');
            text = text.Insert(text.Length - digits, ".");
        }
        return JsValue.From(number < 0 ? "-" + text : text);
    }

    private static double ReadDate(JsValue[] args)
    {
        if (args.Length == 0) return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (args[0].Data is DateObject date) return date.Value;
        var value = AbstractOperations.ToPrimitive(args[0]);
        if (value.Data is string text)
        {
            // ISO date-only strings denote UTC; date-times without an offset use local time.
            var styles = text.Length == 10 && text[4] == '-' && text[7] == '-'
                ? DateTimeStyles.AssumeUniversal : DateTimeStyles.AssumeLocal;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles, out var parsed)
                ? parsed.ToUnixTimeMilliseconds() : double.NaN;
        }
        var timestamp = value.ToNumber();
        return Math.Abs(timestamp) <= 8_640_000_000_000_000d ? Math.Truncate(timestamp) : double.NaN;
    }

    private static double RequireDate(JsValue receiver) => receiver.Data is DateObject date
        ? date.Value : throw JsException.TypeError("Date method requires a Date receiver");

    private static string FormatIsoDate(double timestamp)
    {
        if (double.IsNaN(timestamp)) throw JsException.RangeError("Invalid time value");
        // Project into a Gregorian cycle supported by DateTimeOffset, then restore the year.
        const long cycle = 146_097L * 86_400_000;
        var milliseconds = (long)timestamp;
        var cycles = Math.DivRem(milliseconds, cycle, out var remainder);
        if (remainder < 0) { cycles--; remainder += cycle; }
        var date = DateTimeOffset.FromUnixTimeMilliseconds(remainder);
        var year = date.Year + cycles * 400;
        var yearText = year is >= 0 and <= 9999 ? year.ToString("D4", CultureInfo.InvariantCulture)
            : (year < 0 ? "-" : "+") + Math.Abs(year).ToString("D6", CultureInfo.InvariantCulture);
        return yearText + date.ToString("'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'", CultureInfo.InvariantCulture);
    }

    private sealed class NumberObject : JsObject
    {
        internal NumberObject(double value, JsObject prototype) { Value = value; Prototype = prototype; }
        internal double Value { get; }
    }

    private sealed class DateObject : JsObject
    {
        internal DateObject(double value, JsObject prototype) { Value = value; Prototype = prototype; }
        internal double Value { get; }
    }
}
