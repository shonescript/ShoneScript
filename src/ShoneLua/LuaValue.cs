// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using ShoneLua.Runtime;

namespace ShoneLua;

public enum LuaValueKind { Nil, Boolean, Number, String, Table, Function }

/// <summary>A Lua value. The default value is nil; all numbers are IEEE 754 binary64.</summary>
public readonly struct LuaValue : IEquatable<LuaValue>
{
    private readonly double _number;
    private readonly object? _reference;

    private LuaValue(LuaValueKind kind, double number = 0, object? reference = null) =>
        (Kind, _number, _reference) = (kind, number, reference);

    public LuaValueKind Kind { get; }
    public static LuaValue Nil => default;
    public static LuaValue FromBoolean(bool value) => new(LuaValueKind.Boolean, value ? 1 : 0);
    public static LuaValue FromNumber(double value) => new(LuaValueKind.Number, value);
    public static LuaValue FromString(string value) => new(LuaValueKind.String, reference: value ?? throw new ArgumentNullException(nameof(value)));
    internal static LuaValue FromTable(LuaTable value) => new(LuaValueKind.Table, reference: value);
    internal static LuaValue FromFunction(LuaFunction value) => new(LuaValueKind.Function, reference: value);

    public bool IsTruthy => Kind != LuaValueKind.Nil && (Kind != LuaValueKind.Boolean || _number != 0);
    public bool AsBoolean() => Kind == LuaValueKind.Boolean ? _number != 0 : throw WrongKind("boolean");
    public double AsNumber() => Kind == LuaValueKind.Number ? _number : throw WrongKind("number");
    public string AsString() => Kind == LuaValueKind.String ? (string)_reference! : throw WrongKind("string");
    internal LuaTable AsTable() => (LuaTable)_reference!;
    internal LuaFunction AsFunction() => (LuaFunction)_reference!;
    private InvalidOperationException WrongKind(string expected) => new($"Expected Lua {expected}, got {Kind}.");

    public bool Equals(LuaValue other) => Kind == other.Kind && Kind switch
    {
        LuaValueKind.Nil => true,
        LuaValueKind.Boolean or LuaValueKind.Number => _number.Equals(other._number),
        LuaValueKind.String => string.Equals((string?)_reference, (string?)other._reference, StringComparison.Ordinal),
        _ => ReferenceEquals(_reference, other._reference)
    };

    public override bool Equals(object? obj) => obj is LuaValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Kind, _number, _reference);
    public static bool operator ==(LuaValue left, LuaValue right) => left.Equals(right);
    public static bool operator !=(LuaValue left, LuaValue right) => !left.Equals(right);

    internal bool LuaEquals(LuaValue other) => Kind == LuaValueKind.Number && other.Kind == LuaValueKind.Number
        ? _number == other._number : Equals(other);

    public override string ToString() => Kind switch
    {
        LuaValueKind.Nil => "nil",
        LuaValueKind.Boolean => _number == 0 ? "false" : "true",
        LuaValueKind.Number => _number switch
        {
            double.PositiveInfinity => "inf",
            double.NegativeInfinity => "-inf",
            _ when double.IsNaN(_number) => "nan",
            _ => _number.ToString("G", CultureInfo.InvariantCulture)
        },
        LuaValueKind.String => AsString(),
        LuaValueKind.Table => "table",
        LuaValueKind.Function => "function",
        _ => throw new InvalidOperationException()
    };
}
