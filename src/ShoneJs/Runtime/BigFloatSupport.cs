// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Sdcb.Arithmetic;

[assembly: InternalsVisibleTo("ShonePy")]

namespace ShoneJs.Runtime;

// Keep both scripting front ends on the same limits without changing Sdcb's global defaults.
internal static partial class BigFloatSupport
{
    internal const int DefaultPrecision = 128;
    internal const int MinPrecision = 2;
    internal const int MaxPrecision = 65_536;
    internal const RoundingMode Rounding = RoundingMode.NearestEven;

    private const int MaxTextLength = 32_768;
    private const int MaxDecimalExponent = 65_536;
    private const int MaxFormatBinaryExponent = 200_000;
    private const int MaxArithmeticBinaryExponent = 1_000_000;

    // Repeated multiplication can exhaust Sdcb's exponent range and wrap around.
    internal static bool IsMagnitudeSupported(BigFloat value) => !value.IsNumber ||
        value.BinaryExponent is >= -MaxArithmeticBinaryExponent and <= MaxArithmeticBinaryExponent;

    [GeneratedRegex(@"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE]([+-]?[0-9]+))?\z", RegexOptions.NonBacktracking)]
    private static partial Regex DecimalPattern();

    internal static BigFloat Parse(string text, int precision)
    {
        if (text.Length > MaxTextLength)
            throw new ArgumentOutOfRangeException(nameof(text), "BigFloat input is too long.");

        text = text.Trim();
        if (text.Equals("NaN", StringComparison.OrdinalIgnoreCase))
            return BigFloat.FromDouble(double.NaN, precision, Rounding);
        if (text.Equals("Infinity", StringComparison.OrdinalIgnoreCase) || text.Equals("+Infinity", StringComparison.OrdinalIgnoreCase))
            return BigFloat.FromDouble(double.PositiveInfinity, precision, Rounding);
        if (text.Equals("-Infinity", StringComparison.OrdinalIgnoreCase))
            return BigFloat.FromDouble(double.NegativeInfinity, precision, Rounding);

        var match = DecimalPattern().Match(text);
        if (!match.Success)
            throw new FormatException("Invalid BigFloat literal.");
        var exponent = match.Groups[1];
        if (exponent.Success && (!int.TryParse(exponent.ValueSpan, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            || value is < -MaxDecimalExponent or > MaxDecimalExponent))
            throw new ArgumentOutOfRangeException(nameof(text), "BigFloat decimal exponent is out of range.");

        return BigFloat.Parse(text, precision, Rounding);
    }

    internal static string Format(BigFloat value)
    {
        if (value.IsNumber && value.BinaryExponent is < -MaxFormatBinaryExponent or > MaxFormatBinaryExponent)
            throw new ArgumentOutOfRangeException(nameof(value), "BigFloat magnitude is out of range for decimal formatting.");
        return value.ToString(0, Rounding);
    }
}
