// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using static ShoneJs.Runtime.BigFloatSupport;

namespace ShoneJs.Runtime.Builtins;

internal static class BigFloatBuiltins
{
    internal static void Install(Action<string, JsValue> setGlobal, JsObject objectPrototype)
    {
        var prototype = new JsObject { Prototype = objectPrototype };
        var factory = new JsFunction(args =>
        {
            var input = Argument(args, 0);
            var fallback = input.Data is BigFloatObject existing ? existing.Value.Precision : DefaultPrecision;
            int precision = ReadPrecision(Argument(args, 1), fallback);
            return Wrap(Convert(input, precision));
        }, 1) { Prototype = objectPrototype };
        factory.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From("BigFloat"), configurable: true));
        factory.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(prototype)));
        prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(factory), writable: true, configurable: true));

        DefineMethod(factory, "pi", 0, (_, args) => Wrap(BigFloat.Pi(ReadPrecision(Argument(args, 0), DefaultPrecision), Rounding)));
        DefineBinary("add", BigFloat.Add);
        DefineBinary("sub", BigFloat.Subtract);
        DefineBinary("mul", BigFloat.Multiply);
        DefineBinary("div", BigFloat.Divide);
        DefineMethod(prototype, "sqrt", 0, (self, _) =>
        {
            var value = RequireValue(self);
            return Wrap(BigFloat.Sqrt(value, value.Precision, Rounding));
        });
        DefineMethod(prototype, "equals", 1, (self, args) => JsValue.From(RequireValue(self) == RequireValue(Argument(args, 0))));
        prototype.DefineOwnProperty("precision", PropertyDescriptor.Accessor(get: CreateFunction("get precision", 0,
            (self, _) => JsValue.From((double)RequireValue(self).Precision))));
        DefineMethod(prototype, "withPrecision", 1, (self, args) =>
            Wrap(RequireValue(self).WithPrecision(ReadPrecision(Argument(args, 0)), Rounding)));
        DefineMethod(prototype, "toString", 0, (self, _) => JsValue.From(FormatValue(RequireValue(self))));
        DefineMethod(prototype, "toNumber", 0, (self, _) => JsValue.From(RequireValue(self).ToDouble(Rounding)));

        // Default and numeric coercion try valueOf before toString, which is also used by String().
        prototype.DefineOwnProperty("valueOf", PropertyDescriptor.Data(CreateFunction("valueOf", 0, (_, _) =>
            throw JsException.TypeError("Use BigFloat methods for arithmetic or toNumber() to convert to Number"))));
        setGlobal("BigFloat", JsValue.From(factory));

        JsValue Wrap(BigFloat value)
        {
            if (!IsMagnitudeSupported(value))
                throw JsException.RangeError("BigFloat result exponent is out of range");
            return JsValue.From(new BigFloatObject(value, prototype));
        }

        void DefineBinary(string name, Func<BigFloat, BigFloat, int, RoundingMode, BigFloat> operation) =>
            DefineMethod(prototype, name, 1, (self, args) =>
            {
                var left = RequireValue(self);
                var right = RequireValue(Argument(args, 0));
                return Wrap(operation(left, right, Math.Max(left.Precision, right.Precision), Rounding));
            });

        void DefineMethod(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call) =>
            target.DefineOwnProperty(name, PropertyDescriptor.Data(CreateFunction(name, arity, call), writable: true, configurable: true));

        JsValue CreateFunction(string name, int arity, Func<JsValue, JsValue[], JsValue> call)
        {
            var function = new JsFunction(call, arity) { Prototype = objectPrototype };
            function.DefineOwnProperty("name", PropertyDescriptor.Data(JsValue.From(name), configurable: true));
            return JsValue.From(function);
        }
    }

    private static JsValue Argument(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;

    private static BigFloat RequireValue(JsValue value) => value.Data is BigFloatObject wrapper
        ? wrapper.Value : throw JsException.TypeError("Expected a BigFloat value");

    private static int ReadPrecision(JsValue bits, int? fallback = null)
    {
        if (bits.Type == JsType.Undefined && fallback.HasValue) return fallback.Value;
        if (bits.Type != JsType.Number) throw JsException.TypeError("BigFloat precision must be a Number");
        double precision = bits.ToNumber();
        if (!double.IsFinite(precision) || precision != Math.Truncate(precision) || precision is < MinPrecision or > MaxPrecision)
            throw JsException.RangeError($"BigFloat precision must be an integer from {MinPrecision} to {MaxPrecision}");
        return (int)precision;
    }

    private static BigFloat Convert(JsValue value, int precision)
    {
        return value.Data switch
        {
            BigFloatObject wrapper => wrapper.Value.WithPrecision(precision, Rounding),
            double number => BigFloat.FromDouble(number, precision, Rounding),
            BigInt integer => BigFloat.FromBigInt(integer, precision, Rounding),
            string text => ParseText(text, precision),
            _ => throw JsException.TypeError("BigFloat requires a decimal string, Number, BigInt or BigFloat")
        };
    }

    private static BigFloat ParseText(string text, int precision)
    {
        try { return Parse(text, precision); }
        catch (FormatException error) { throw new JsException(JsValue.From(new JsError("SyntaxError", error.Message))); }
        catch (ArgumentOutOfRangeException error) { throw JsException.RangeError(error.Message); }
    }

    private static string FormatValue(BigFloat value)
    {
        try { return Format(value); }
        catch (ArgumentOutOfRangeException error) { throw JsException.RangeError(error.Message); }
    }

    private sealed class BigFloatObject : JsObject
    {
        internal BigFloatObject(BigFloat value, JsObject prototype)
        {
            Value = value;
            Prototype = prototype;
            PreventExtensions();
        }

        internal BigFloat Value { get; }
    }
}
