// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sdcb.Arithmetic;

namespace ShonePy.Modules;

public static partial class PyDateTimeModule
{
    private const long MicrosecondsPerSecond = 1_000_000;
    private const long MicrosecondsPerDay = 86_400 * MicrosecondsPerSecond;
    private const int MaxDeltaDays = 999_999_999;
    private static readonly long MaxOrdinal = DateOnly.MaxValue.DayNumber + 1L;
    private static readonly long MaxDateTime = MaxOrdinal * MicrosecondsPerDay - 1;
    private static readonly BigInt MinDelta = new BigInt(-MaxDeltaDays) * new BigInt(MicrosecondsPerDay);
    private static readonly BigInt MaxDelta = new BigInt(MaxDeltaDays + 1) * new BigInt(MicrosecondsPerDay) - 1;
    private static readonly string[] DateFields = ["year", "month", "day"];
    private static readonly string[] TimeFields = ["hour", "minute", "second", "microsecond", "tzinfo", "fold"];
    private static readonly string[] DeltaFields = ["days", "seconds", "microseconds", "milliseconds", "minutes", "hours", "weeks"];
    private static readonly long[] DeltaUnits = [MicrosecondsPerDay, MicrosecondsPerSecond, 1, 1_000, 60 * MicrosecondsPerSecond, 3_600 * MicrosecondsPerSecond, 7 * MicrosecondsPerDay];
    private static readonly PyValue[] Midnight = [0, 0, 0, 0, PyValue.None, 0];
    private static readonly PyValue[] RequiredDate = [PyValue.None, PyValue.None, PyValue.None];

    internal enum Kind { date, time, datetime, timedelta }

    public static PyDict CreateModule()
    {
        var date = new PyDict
        {
            ["min"] = Date(DateOnly.MinValue), ["max"] = Date(DateOnly.MaxValue),
            ["resolution"] = Delta(new BigInt(MicrosecondsPerDay)),
            ["today"] = NoArgs("today", () => Date(DateOnly.FromDateTime(DateTime.Now))),
            ["fromisoformat"] = OneArg("fromisoformat", value => Date(ParseDate(Text(value)))),
            ["fromordinal"] = OneArg("fromordinal", value => Date(FromOrdinal(value)))
        };
        var time = new PyDict
        {
            ["min"] = Time(TimeOnly.MinValue), ["max"] = Time(TimeOnly.MaxValue),
            ["resolution"] = Delta(BigInt.One),
            ["fromisoformat"] = OneArg("fromisoformat", value => Time(ParseTime(Text(value), prefixAllowed: true)))
        };
        var datetime = new PyDict
        {
            ["min"] = DateTimeValue(DateTime.MinValue), ["max"] = DateTimeValue(DateTime.MaxValue),
            ["resolution"] = Delta(BigInt.One),
            ["today"] = NoArgs("today", Now),
            ["now"] = Bind("now", ["tz"], [PyValue.None], args =>
            {
                RequireNaive(args[0]);
                return Now();
            }),
            ["fromisoformat"] = OneArg("fromisoformat", value => ParseDateTime(Text(value))),
            ["fromordinal"] = OneArg("fromordinal", value => DateTimeValue(FromOrdinal(value).ToDateTime(TimeOnly.MinValue))),
            ["combine"] = Bind("combine", ["date", "time", "tzinfo"], [PyValue.None, PyValue.None, PyValue.None], args =>
            {
                if (args[0].Data is not Value { Type: Kind.date or Kind.datetime } day || args[1].Data is not Value { Type: Kind.time } clock)
                    throw Error("TypeError", "combine() requires a date and a time");
                RequireNaive(args[2]);
                return DateTimeValue(day.Day.ToDateTime(clock.Clock), clock.Fold);
            }, required: 2)
        };
        var delta = new PyDict
        {
            ["min"] = Delta(MinDelta), ["max"] = Delta(MaxDelta), ["resolution"] = Delta(BigInt.One)
        };
        return new PyDict
        {
            ["MINYEAR"] = 1, ["MAXYEAR"] = 9999,
            ["date"] = Bind("date", DateFields, RequiredDate, args => Date(BuildDate(args)), required: 3, attributes: date),
            ["time"] = Bind("time", TimeFields, Midnight, args => Time(BuildTime(args), Fold(args[5])), keywordOnly: 5, attributes: time),
            ["datetime"] = Bind("datetime", [.. DateFields, .. TimeFields], [.. RequiredDate, .. Midnight], BuildDateTime, required: 3, keywordOnly: 8, attributes: datetime),
            ["timedelta"] = Bind("timedelta", DeltaFields, [0, 0, 0, 0, 0, 0, 0], BuildDelta, attributes: delta)
        };
    }

    // Dates and times share a microsecond coordinate; durations require a wider range than TimeSpan.
    internal sealed class Value(Kind type, BigInt microseconds, int fold = 0) : IEquatable<Value>
    {
        internal Kind Type { get; } = type;
        internal BigInt Microseconds { get; } = microseconds;
        internal int Fold { get; } = fold;
        internal bool IsTruthy => Type != Kind.timedelta || !Microseconds.IsZero;
        internal DateOnly Day => DateOnly.FromDayNumber((int)((long)Microseconds / MicrosecondsPerDay));
        internal TimeOnly Clock => new((long)Microseconds % MicrosecondsPerDay * 10);
        private long DeltaDays => (long)FloorDivide(Microseconds, new BigInt(MicrosecondsPerDay));
        private long DeltaRemainder => (long)(Microseconds - new BigInt(DeltaDays) * new BigInt(MicrosecondsPerDay));
        private int Microsecond => Clock.Microsecond + 1000 * Clock.Millisecond;

        public bool Equals(Value? other) => other is not null && Type == other.Type && Microseconds == other.Microseconds;
        public override bool Equals(object? other) => other is Value value && Equals(value);
        public override int GetHashCode() => HashCode.Combine(Type, Microseconds);

        internal PyValue GetAttribute(string name)
        {
            if (Type is Kind.date or Kind.datetime)
            {
                switch (name)
                {
                    case "year": return Day.Year;
                    case "month": return Day.Month;
                    case "day": return Day.Day;
                    case "toordinal": return NoArgs(name, () => Day.DayNumber + 1);
                    case "weekday": return NoArgs(name, () => (Day.DayNumber % 7));
                    case "isoweekday": return NoArgs(name, () => (Day.DayNumber % 7) + 1);
                }
            }
            if (Type is Kind.time or Kind.datetime)
            {
                switch (name)
                {
                    case "hour": return Clock.Hour;
                    case "minute": return Clock.Minute;
                    case "second": return Clock.Second;
                    case "microsecond": return Microsecond;
                    case "fold": return Fold;
                    case "tzinfo": return PyValue.None;
                    case "utcoffset" or "dst" or "tzname": return NoArgs(name, () => PyValue.None);
                }
            }
            return (Type, name) switch
            {
                (Kind.date, "isoformat") => NoArgs(name, () => FormatDay()),
                (Kind.time, "isoformat") => Bind(name, ["timespec"], ["auto"], args => FormatTime(Text(args[0]))),
                (Kind.datetime, "isoformat") => Bind(name, ["sep", "timespec"], ["T", "auto"], args => FormatDateTime(Text(args[0]), Text(args[1]))),
                (Kind.date, "replace") => Bind(name, DateFields, DateParts(), args => Date(BuildDate(args))),
                (Kind.time, "replace") => Bind(name, TimeFields, TimeParts(), args => Time(BuildTime(args), PyDateTimeModule.Fold(args[5])), keywordOnly: 5),
                (Kind.datetime, "replace") => Bind(name, [.. DateFields, .. TimeFields], [.. DateParts(), .. TimeParts()], BuildDateTime, keywordOnly: 8),
                (Kind.datetime, "date") => NoArgs(name, () => Date(Day)),
                (Kind.datetime, "time") => NoArgs(name, () => Time(Clock, Fold)),
                (Kind.datetime, "timetz") => NoArgs(name, () => Time(Clock, Fold)),
                (Kind.timedelta, "days") => DeltaDays,
                (Kind.timedelta, "seconds") => DeltaRemainder / MicrosecondsPerSecond,
                (Kind.timedelta, "microseconds") => DeltaRemainder % MicrosecondsPerSecond,
                (Kind.timedelta, "total_seconds") => NoArgs(name, () => FloatQuotient(Microseconds, new BigInt(MicrosecondsPerSecond))),
                _ => throw Error("AttributeError", $"'{Type}' object has no attribute '{name}'")
            };
        }

        private PyValue[] DateParts() => [Day.Year, Day.Month, Day.Day];
        private PyValue[] TimeParts() => [Clock.Hour, Clock.Minute, Clock.Second, Microsecond, PyValue.None, Fold];
        private string FormatDay() => Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private string FormatDateTime(string separator, string timespec)
        {
            if (separator.EnumerateRunes().Count() != 1)
                throw Error("TypeError", "isoformat() separator must be one character");
            return FormatDay() + separator + FormatTime(timespec);
        }

        private string FormatTime(string timespec) => timespec switch
        {
            "hours" => Clock.ToString("HH", CultureInfo.InvariantCulture),
            "minutes" => Clock.ToString("HH:mm", CultureInfo.InvariantCulture),
            "seconds" => Clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "auto" when Microsecond == 0 => Clock.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "milliseconds" => Clock.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            "microseconds" or "auto" => Clock.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
            _ => throw Error("ValueError", "Unknown timespec value")
        };

        public override string ToString()
        {
            if (Type == Kind.date) return FormatDay();
            if (Type == Kind.time) return FormatTime("auto");
            if (Type == Kind.datetime) return FormatDateTime(" ", "auto");
            long days = DeltaDays;
            long seconds = DeltaRemainder / MicrosecondsPerSecond;
            long fraction = DeltaRemainder % MicrosecondsPerSecond;
            string prefix = days == 0 ? "" : FormattableString.Invariant($"{days} day{(Math.Abs(days) == 1 ? "" : "s")}, ");
            string clock = FormattableString.Invariant($"{seconds / 3600}:{seconds / 60 % 60:D2}:{seconds % 60:D2}");
            return prefix + clock + (fraction == 0 ? "" : FormattableString.Invariant($".{fraction:D6}"));
        }

        internal string Repr()
        {
            string[] parts = Type switch
            {
                Kind.date => DateParts().Select(x => x.ToPythonString()).ToArray(),
                Kind.time => ClockRepr(),
                Kind.datetime => [.. DateParts().Select(x => x.ToPythonString()), .. ClockRepr()],
                _ => DeltaRepr()
            };
            return $"datetime.{Type}({string.Join(", ", parts)})";
        }

        private string[] ClockRepr()
        {
            var values = new List<int> { Clock.Hour, Clock.Minute };
            if (Clock.Second != 0 || Microsecond != 0) values.Add(Clock.Second);
            if (Microsecond != 0) values.Add(Microsecond);
            var result = values.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToList();
            if (Fold != 0) result.Add("fold=1");
            return result.ToArray();
        }

        private string[] DeltaRepr()
        {
            var result = new List<string>();
            foreach (var name in DeltaFields.Take(3))
            {
                var part = GetAttribute(name);
                if (part.IsTruthy()) result.Add($"{name}={part.ToPythonString()}");
            }
            return result.Count == 0 ? ["0"] : result.ToArray();
        }
    }

    internal static PyValue? Unary(string op, Value value) => (op, value.Type) switch
    {
        ("+", Kind.timedelta) => Delta(value.Microseconds),
        ("-", Kind.timedelta) => Delta(-value.Microseconds),
        ("abs", Kind.timedelta) => Delta(BigInt.Abs(value.Microseconds)),
        _ => (PyValue?)null
    };

    internal static PyValue? Binary(PyValue left, string op, PyValue right)
    {
        if (left.Data is Value a && right.Data is Value b)
        {
            if (a.Type == Kind.timedelta && b.Type == Kind.timedelta)
            {
                BigInt x = a.Microseconds;
                BigInt y = b.Microseconds;
                return op switch
                {
                    "+" => Delta(x + y), "-" => Delta(x - y),
                    "/" => PyValue.From(FloatQuotient(x, NonZero(y))),
                    "//" => PyValue.From(FloorDivide(x, NonZero(y))),
                    "%" => Delta(x - FloorDivide(x, NonZero(y)) * y),
                    _ => (PyValue?)null
                };
            }
            if (a.Type == b.Type && a.Type is Kind.date or Kind.datetime && op == "-")
                return Delta(a.Microseconds - b.Microseconds);
            if (a.Type is Kind.date or Kind.datetime && b.Type == Kind.timedelta && op is "+" or "-")
                return Shift(a, b, op == "+");
            if (a.Type == Kind.timedelta && b.Type is Kind.date or Kind.datetime && op == "+")
                return Shift(b, a, add: true);
            return null;
        }
        if (left.Data is Value { Type: Kind.timedelta } delta)
            return Scale(delta.Microseconds, op, right);
        if (right.Data is Value { Type: Kind.timedelta } reversed && op == "*")
            return Scale(reversed.Microseconds, op, left);
        return null;
    }

    private static PyValue Shift(Value date, Value delta, bool add)
    {
        var offset = delta.Microseconds;
        if (date.Type == Kind.date)
        {
            var unit = new BigInt(MicrosecondsPerDay);
            offset = FloorDivide(offset, unit) * unit;
        }
        var result = date.Microseconds + (add ? offset : -offset);
        if (result < BigInt.Zero || result > new BigInt(MaxDateTime))
            throw Error("OverflowError", "date value out of range");
        return PyValue.From(new Value(date.Type, result));
    }

    private static PyValue? Scale(BigInt microseconds, string op, PyValue value)
    {
        if (value.Type is PyType.Integer or PyType.Boolean)
        {
            var factor = Integer(value);
            return op switch
            {
                "*" => Delta(microseconds * factor),
                "/" => Delta(RoundRatio(microseconds, NonZero(factor))),
                "//" => Delta(FloorDivide(microseconds, NonZero(factor))),
                _ => (PyValue?)null
            };
        }
        if (value.Type == PyType.Float && op is "*" or "/")
        {
            var (numerator, denominator) = FloatRatio((double)value.Data!);
            return Delta(op == "*" ? RoundRatio(microseconds * numerator, denominator) : RoundRatio(microseconds * denominator, NonZero(numerator)));
        }
        return null;
    }

    private static PyValue BuildDelta(PyValue[] args)
    {
        BigInt total = BigInt.Zero;
        double remainder = 0;
        for (int i = 0; i < args.Length; i++)
        {
            var unit = new BigInt(DeltaUnits[i]);
            if (args[i].Type != PyType.Float)
            {
                total += Integer(args[i]) * unit;
                continue;
            }
            var number = (double)args[i].Data!;
            CheckFinite(number);
            var whole = Math.Truncate(number);
            total += new BigInt(whole) * unit;
            var fraction = (number - whole) * DeltaUnits[i];
            var wholeFraction = Math.Truncate(fraction);
            total += new BigInt(wholeFraction);
            remainder += fraction - wholeFraction;
        }
        var rounded = Math.Round(remainder, MidpointRounding.ToEven);
        if (Math.Abs(remainder - rounded) == 0.5 && !(total & BigInt.One).IsZero)
            rounded += Math.Sign(remainder - rounded);
        return Delta(total + new BigInt(rounded));
    }

    // Round the exact quotient once. Both duration magnitudes are within the normal double range.
    private static double FloatQuotient(BigInt numerator, BigInt denominator)
    {
        bool negative = (numerator.Sign < 0) != (denominator.Sign < 0);
        if (numerator.IsZero) return negative ? -0.0 : 0.0;
        numerator = BigInt.Abs(numerator);
        denominator = BigInt.Abs(denominator);
        int exponent = (int)(numerator.GetBitLength() - denominator.GetBitLength());
        if ((exponent >= 0 && numerator < (denominator << exponent)) ||
            (exponent < 0 && (numerator << -exponent) < denominator))
            exponent--;
        int shift = 52 - exponent;
        var rounded = shift >= 0 ? RoundRatio(numerator << shift, denominator) : RoundRatio(numerator, denominator << -shift);
        double result = Math.ScaleB((double)rounded, -shift);
        return negative ? -result : result;
    }

    private static (BigInt Numerator, BigInt Denominator) FloatRatio(double number)
    {
        CheckFinite(number);
        long bits = BitConverter.DoubleToInt64Bits(number);
        int exponent = (int)((bits >> 52) & 0x7ff);
        long significand = (bits & ((1L << 52) - 1)) | (exponent == 0 ? 0 : 1L << 52);
        var numerator = new BigInt(bits < 0 ? -significand : significand);
        int shift = Math.Max(1, exponent) - 1075;
        return shift >= 0 ? (numerator << shift, BigInt.One) : (numerator, BigInt.One << -shift);
    }

    private static void CheckFinite(double value)
    {
        if (double.IsNaN(value)) throw Error("ValueError", "cannot convert float NaN to integer");
        if (double.IsInfinity(value)) throw Error("OverflowError", "cannot convert float infinity to integer");
    }

    private static BigInt RoundRatio(BigInt numerator, BigInt denominator)
    {
        if (denominator.Sign < 0) { numerator = -numerator; denominator = -denominator; }
        var quotient = FloorDivide(numerator, denominator);
        var twiceRemainder = (numerator - quotient * denominator) * new BigInt(2);
        return twiceRemainder > denominator || (twiceRemainder == denominator && !(quotient & BigInt.One).IsZero) ? quotient + 1 : quotient;
    }

    private static BigInt FloorDivide(BigInt numerator, BigInt denominator)
    {
        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        return !remainder.IsZero && remainder.Sign != denominator.Sign ? quotient - 1 : quotient;
    }

    private static BigInt NonZero(BigInt divisor) => !divisor.IsZero ? divisor : throw Error("ZeroDivisionError", "division by zero");

    private static PyValue Now()
    {
        var now = DateTimeOffset.Now;
        var local = now.DateTime;
        var zone = TimeZoneInfo.Local;
        int fold = zone.IsAmbiguousTime(local) && now.Offset == zone.GetAmbiguousTimeOffsets(local).Min() ? 1 : 0;
        return DateTimeValue(local, fold);
    }

    private static PyValue Date(DateOnly day) => PyValue.From(new Value(Kind.date, new BigInt(day.DayNumber * MicrosecondsPerDay)));
    private static PyValue Time(TimeOnly time, int fold = 0) => PyValue.From(new Value(Kind.time, new BigInt(time.Ticks / 10), fold));
    private static PyValue DateTimeValue(DateTime datetime, int fold = 0) => PyValue.From(new Value(Kind.datetime, new BigInt(datetime.Ticks / 10), fold));
    private static PyValue Delta(BigInt microseconds) => microseconds >= MinDelta && microseconds <= MaxDelta
        ? PyValue.From(new Value(Kind.timedelta, microseconds))
        : throw Error("OverflowError", "timedelta days out of range");

    private static DateOnly BuildDate(PyValue[] args)
    {
        var year = Range(args[0], 1, 9999, "year");
        var month = Range(args[1], 1, 12, "month");
        return new DateOnly(year, month, Range(args[2], 1, DateTime.DaysInMonth(year, month), "day"));
    }

    private static TimeOnly BuildTime(PyValue[] args)
    {
        int hour = Range(args[0], 0, 23, "hour");
        int minute = Range(args[1], 0, 59, "minute");
        int second = Range(args[2], 0, 59, "second");
        int microsecond = Range(args[3], 0, 999_999, "microsecond");
        RequireNaive(args[4]);
        return new TimeOnly(hour, minute, second, microsecond / 1000, microsecond % 1000);
    }

    private static PyValue BuildDateTime(PyValue[] args) => DateTimeValue(BuildDate(args).ToDateTime(BuildTime(args[3..])), Fold(args[8]));
    private static int Fold(PyValue value) => Range(value, 0, 1, "fold");
    private static DateOnly FromOrdinal(PyValue value) => DateOnly.FromDayNumber(Range(value, 1, (int)MaxOrdinal, "ordinal") - 1);
    private static void RequireNaive(PyValue tz)
    {
        if (tz.Type != PyType.None) throw Error("TypeError", "only None is supported for tzinfo");
    }

    private static int Range(PyValue value, int min, int max, string field)
    {
        var number = Integer(value);
        if (number < new BigInt(int.MinValue) || number > new BigInt(int.MaxValue))
            throw Error("OverflowError", $"{field} does not fit in an integer");
        int result = (int)number;
        if (result < min || result > max) throw Error("ValueError", $"{field} must be in {min}..{max}");
        return result;
    }

    private static BigInt Integer(PyValue value) => value.Type switch
    {
        PyType.Integer => (BigInt)value.Data!,
        PyType.Boolean => value.IsTruthy() ? BigInt.One : BigInt.Zero,
        _ => throw Error("TypeError", $"a number of the required type was expected, got '{PyOperations.TypeName(value)}'")
    };

    private static string Text(PyValue value) => value.Type == PyType.String ? (string)value.Data!
        : throw Error("TypeError", $"expected str, got '{PyOperations.TypeName(value)}'");

    private static DateOnly ParseDate(string text)
    {
        if (DateOnly.TryParseExact(text, ["yyyy-MM-dd", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        var week = IsoWeekDate().Match(text);
        if (week.Success)
        {
            int year = Digits(week, "year");
            int number = Digits(week, "week");
            int day = week.Groups["day"].Success ? Digits(week, "day") : 1;
            if (year != 0 && day is >= 1 and <= 7 && number >= 1 && number <= ISOWeek.GetWeeksInYear(year))
            {
                try { return DateOnly.FromDateTime(ISOWeek.ToDateTime(year, number, (DayOfWeek)(day % 7))); }
                catch (ArgumentOutOfRangeException) { }
            }
        }
        throw InvalidIso(text);
    }

    private static TimeOnly ParseTime(string text, bool prefixAllowed = false)
    {
        var match = IsoTime().Match(prefixAllowed && text.StartsWith('T') ? text[1..] : text);
        if (!match.Success) throw InvalidIso(text);
        int hour = Digits(match, "hour");
        int minute = Digits(match, "minute");
        int second = Digits(match, "second");
        if (hour > 23 || minute > 59 || second > 59) throw InvalidIso(text);
        var fraction = match.Groups["fraction"].ValueSpan;
        int microseconds = 0;
        for (int i = 0; i < 6; i++)
            microseconds = microseconds * 10 + (i < fraction.Length ? fraction[i] - '0' : 0);
        return new TimeOnly(hour, minute, second, microseconds / 1000, microseconds % 1000);
    }

    private static PyValue ParseDateTime(string text)
    {
        var match = IsoDateTime().Match(text);
        if (!match.Success) throw InvalidIso(text);
        var day = ParseDate(match.Groups["date"].Value);
        var time = match.Groups["time"];
        return DateTimeValue(day.ToDateTime(time.Success ? ParseTime(time.Value) : TimeOnly.MinValue));
    }

    private static int Digits(Match match, string name) => match.Groups[name].Success
        ? int.Parse(match.Groups[name].ValueSpan, CultureInfo.InvariantCulture) : 0;

    private static PythonException InvalidIso(string text) => Error("ValueError", $"Invalid isoformat string: {PyValue.From(text).Repr()}");
    private static PythonException Error(string name, string message) => new(name, message);

    [GeneratedRegex(@"\A(?<year>[0-9]{4})(?<sep>-?)W(?<week>[0-9]{2})(?:\k<sep>(?<day>[0-9]))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoWeekDate();

    [GeneratedRegex(@"\A(?<hour>[0-9]{2})(?:(?<sep>:?)(?<minute>[0-9]{2})(?:\k<sep>(?<second>[0-9]{2}))?)?(?:[.,](?<fraction>[0-9]+))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTime();

    [GeneratedRegex(@"\A(?<date>[0-9]{4}(?:-[0-9]{2}-[0-9]{2}|[0-9]{4}|-W[0-9]{2}(?:-[0-9])?|W[0-9]{2}[0-9]?))(?:(?:[\uD800-\uDBFF][\uDC00-\uDFFF]|[\s\S])(?<time>[0-9][\s\S]*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateTime();

    private static PyValue NoArgs(string name, Func<PyValue> invoke) => Bind(name, [], [], _ => invoke());
    private static PyValue OneArg(string name, Func<PyValue, PyValue> invoke) => PyValue.From(new PyCallable(args =>
        args.Count == 1 ? invoke(args[0]) : throw Error("TypeError", $"{name}() takes exactly one argument"), name));

    private static PyValue Bind(string name, string[] parameters, PyValue[] defaults, Func<PyValue[], PyValue> invoke,
        int required = 0, int? keywordOnly = null, PyDict? attributes = null)
    {
        return PyValue.From(new PyCallable(args => Call(args, null), name)
        {
            InvokeWithKeywords = Call,
            Attributes = attributes
        });

        PyValue Call(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords)
        {
            int positionalLimit = keywordOnly ?? parameters.Length;
            if (args.Count > positionalLimit) throw Error("TypeError", $"{name}() takes at most {positionalLimit} positional arguments");
            var values = (PyValue[])defaults.Clone();
            for (int i = 0; i < args.Count; i++) values[i] = args[i];
            if (keywords != null)
            {
                foreach (var (key, value) in keywords)
                {
                    int index = Array.IndexOf(parameters, key);
                    if (index < 0) throw Error("TypeError", $"{name}() got an unexpected keyword argument '{key}'");
                    if (index < args.Count) throw Error("TypeError", $"{name}() got multiple values for argument '{key}'");
                    values[index] = value;
                }
            }
            for (int i = args.Count; i < required; i++)
                if (keywords == null || !keywords.ContainsKey(parameters[i]))
                    throw Error("TypeError", $"{name}() missing required argument '{parameters[i]}'");
            return invoke(values);
        }
    }
}
