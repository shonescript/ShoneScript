using System.Globalization;
using Sdcb.Arithmetic;

namespace ShoneJS.Runtime;

public enum JsType { Undefined, Null, Boolean, Number, String, Object, Function, BigInt, Symbol }

public sealed class JsSymbol(string? description = null)
{
    public string? Description { get; } = description;
    public override string ToString() => $"Symbol({Description})";
}

public readonly struct JsValue
{
    private readonly object? _reference;
    private readonly double _number;
    public JsType Type { get; }
    public object? Data => Type switch
    {
        JsType.Number => _number,
        JsType.Boolean => _number != 0,
        _ => _reference
    };

    private JsValue(JsType type, object? reference = null, double number = 0)
    {
        Type = type;
        _reference = reference;
        _number = number;
    }

    public static JsValue Undefined => default;
    public static JsValue Null => new(JsType.Null);
    public static JsValue From(bool value) => new(JsType.Boolean, number: value ? 1 : 0);
    public static JsValue From(double value) => new(JsType.Number, number: value);
    public static JsValue From(string value) => new(JsType.String, value ?? throw new ArgumentNullException(nameof(value)));
    public static JsValue From(JsObject value) => new(value is JsFunction ? JsType.Function : JsType.Object, value ?? throw new ArgumentNullException(nameof(value)));
    public static JsValue From(JsFunction value) => From((JsObject)value);
    public static JsValue From(BigInt value) => new(JsType.BigInt, value);
    public static JsValue From(JsSymbol value) => new(JsType.Symbol, value ?? throw new ArgumentNullException(nameof(value)));

    public bool ToBoolean() => Type switch
    {
        JsType.Undefined or JsType.Null => false,
        JsType.Boolean => _number != 0,
        JsType.Number => _number != 0 && !double.IsNaN(_number),
        JsType.String => ((string)_reference!).Length != 0,
        JsType.BigInt => !((BigInt)_reference!).IsZero,
        _ => true
    };

    public double ToNumber() => Type switch
    {
        JsType.Null => 0,
        JsType.Boolean or JsType.Number => _number,
        JsType.String => AbstractOperations.StringToNumber((string)_reference!),
        JsType.BigInt or JsType.Symbol => throw JsException.TypeError($"Cannot convert {Type} to Number"),
        _ => double.NaN
    };

    public string ToJsString() => Type switch
    {
        JsType.Undefined => "undefined",
        JsType.Null => "null",
        JsType.Boolean => _number != 0 ? "true" : "false",
        JsType.Number => AbstractOperations.NumberToString(_number),
        JsType.String => (string)_reference!,
        JsType.BigInt => ((BigInt)_reference!).ToString(),
        JsType.Symbol => throw JsException.TypeError("Cannot convert a Symbol to a string"),
        JsType.Function => "function",
        _ => "[object Object]"
    };

    public override string ToString() => Type == JsType.Symbol ? _reference!.ToString()! : ToJsString();
}
