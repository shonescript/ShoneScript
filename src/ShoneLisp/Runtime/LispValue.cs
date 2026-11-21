// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace ShoneLisp.Runtime;

public enum LispValueKind
{
    Unspecified, EmptyList, Boolean, Number, String, Symbol, Pair, Procedure,
}

/// <summary>An immutable Lisp value. Pairs can form proper or dotted lists.</summary>
public sealed class LispValue
{
    private readonly object? _value;
    private LispValue(LispValueKind kind, object? value = null) => (Kind, _value) = (kind, value);

    public LispValueKind Kind { get; }
    public bool IsUnspecified => Kind == LispValueKind.Unspecified;
    public static LispValue Unspecified { get; } = new(LispValueKind.Unspecified);
    public static LispValue EmptyList { get; } = new(LispValueKind.EmptyList);
    public static LispValue True { get; } = new(LispValueKind.Boolean, true);
    public static LispValue False { get; } = new(LispValueKind.Boolean, false);

    public static LispValue FromBoolean(bool value) => value ? True : False;
    public static LispValue FromNumber(double value) => double.IsFinite(value)
        ? new(LispValueKind.Number, value)
        : throw new ArgumentOutOfRangeException(nameof(value), "A number must be finite.");
    public static LispValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(LispValueKind.String, value);
    }
    public static LispValue FromSymbol(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new(LispValueKind.Symbol, value);
    }
    public static LispValue Cons(LispValue car, LispValue cdr)
    {
        ArgumentNullException.ThrowIfNull(car);
        ArgumentNullException.ThrowIfNull(cdr);
        return new(LispValueKind.Pair, (car, cdr));
    }

    public bool AsBoolean() => Get<bool>(LispValueKind.Boolean);
    public double AsNumber() => Get<double>(LispValueKind.Number);
    public string AsString() => Get<string>(LispValueKind.String);
    public string AsSymbol() => Get<string>(LispValueKind.Symbol);
    public LispValue Car => Get<(LispValue Car, LispValue Cdr)>(LispValueKind.Pair).Car;
    public LispValue Cdr => Get<(LispValue Car, LispValue Cdr)>(LispValueKind.Pair).Cdr;

    internal static LispValue FromProcedure(LispProcedure procedure) => new(LispValueKind.Procedure, procedure);
    internal LispProcedure AsProcedure() => Get<LispProcedure>(LispValueKind.Procedure);
    private T Get<T>(LispValueKind kind) => Kind == kind
        ? (T)_value!
        : throw new InvalidOperationException($"Expected {kind}, got {Kind}.");

    /// <summary>Formats data using write-style notation (including quotes around strings).</summary>
    public override string ToString() => Format(write: true);

    internal string Format(bool write, Action? visit = null)
    {
        var output = new StringBuilder();
        var pending = new Stack<(LispValue? Value, bool IsTail)>();
        pending.Push((this, false));
        while (pending.TryPop(out var item))
        {
            var (value, isTail) = item;
            if (value is null)
            {
                output.Append(')');
                continue;
            }
            visit?.Invoke();
            if (isTail)
            {
                if (value.Kind == LispValueKind.EmptyList)
                    continue;
                if (value.Kind == LispValueKind.Pair)
                {
                    output.Append(' ');
                    pending.Push((value.Cdr, true));
                    pending.Push((value.Car, false));
                }
                else
                {
                    output.Append(" . ");
                    pending.Push((value, false));
                }
                continue;
            }

            switch (value.Kind)
            {
                case LispValueKind.Pair:
                    output.Append('(');
                    pending.Push((null, false));
                    pending.Push((value.Cdr, true));
                    pending.Push((value.Car, false));
                    break;
                case LispValueKind.String when write:
                    AppendString(output, value.AsString());
                    break;
                default:
                    output.Append(value.Kind switch
                    {
                        LispValueKind.Unspecified => "#<unspecified>",
                        LispValueKind.EmptyList => "()",
                        LispValueKind.Boolean => value.AsBoolean() ? "#t" : "#f",
                        LispValueKind.Number => value.AsNumber().ToString("R", CultureInfo.InvariantCulture),
                        LispValueKind.String => value.AsString(),
                        LispValueKind.Symbol => value.AsSymbol(),
                        LispValueKind.Procedure => "#<procedure>",
                        _ => throw new InvalidOperationException(),
                    });
                    break;
            }
        }
        return output.ToString();
    }

    private static void AppendString(StringBuilder output, string value)
    {
        output.Append('"');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                default: output.Append(ch); break;
            }
        }
        output.Append('"');
    }
}
