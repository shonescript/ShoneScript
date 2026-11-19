namespace ShoneJS.Runtime;

public enum JsType { Undefined, Null, Boolean, Number, String, Object, Function }
public readonly struct JsValue
{
    public JsType Type { get; }
    public object? Data { get; }
    private JsValue(JsType type, object? data) { Type = type; Data = data; }
    public static JsValue Undefined => new(JsType.Undefined, null);
    public static JsValue Null => new(JsType.Null, null);
    public static JsValue From(bool value) => new(JsType.Boolean, value);
    public static JsValue From(double value) => new(JsType.Number, value);
    public static JsValue From(string value) => new(JsType.String, value);
    public static JsValue From(JsObject value) => new(JsType.Object, value);
    public static JsValue From(JsFunction value) => new(JsType.Function, value);
    public bool ToBoolean() => Type switch { JsType.Undefined or JsType.Null => false, JsType.Boolean => (bool)Data!, JsType.Number => (double)Data! != 0 && !double.IsNaN((double)Data!), JsType.String => ((string)Data!).Length != 0, _ => true };
    public double ToNumber() => Type switch { JsType.Undefined => double.NaN, JsType.Null => 0, JsType.Boolean => (bool)Data! ? 1 : 0, JsType.Number => (double)Data!, JsType.String => double.TryParse((string)Data!, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : string.IsNullOrWhiteSpace((string)Data!) ? 0 : double.NaN, _ => double.NaN };
    public string ToJsString() => Type switch { JsType.Undefined => "undefined", JsType.Null => "null", JsType.Boolean => (bool)Data! ? "true" : "false", JsType.Number => ((double)Data!).ToString("G", System.Globalization.CultureInfo.InvariantCulture), JsType.String => (string)Data!, JsType.Function => "function", _ => "[object Object]" };
    public override string ToString() => ToJsString();
}
