// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShonePy.Modules;

namespace ShonePy.Tests;

public sealed class PyDateTimeModuleTests
{
    [Fact]
    public void DateSupportsCalendarFieldsOrdinalsAndIsoWeekDates()
    {
        Assert.Equal(PyValue.From(new PyTuple(2024, 2, 29, 3, 4, "2024-02-29", "2024-02-29", "datetime.date(2024, 2, 29)")), Evaluate("""
            d = dt.date(day=29, month=2, year=2024)
            (d.year, d.month, d.day, d.weekday(), d.isoweekday(),
             dt.date.fromordinal(d.toordinal()).isoformat(),
             dt.date.fromisoformat('2024-W09-4').isoformat(), repr(d))
            """));
        Assert.Equal(PyValue.True, Evaluate("""
            d = dt.date(2024, 2, 29)
            d.replace(day=28) < d and d is not dt.date.fromisoformat('20240229') and {d: 5}[dt.date.fromisoformat('20240229')] == 5
            """));
    }

    [Fact]
    public void TimeAndDateTimePreserveMicrosecondsAndFold()
    {
        Assert.Equal(PyValue.From(new PyTuple("2024-02-29🕰12:34:56.123", "12:34:56.123456", 123456, 1, 0, PyValue.None)), Evaluate("""
            t = dt.time.fromisoformat('T123456,123456789').replace(fold=1)
            d = dt.datetime.combine(dt.date(2024, 2, 29), t)
            (d.isoformat('🕰', timespec='milliseconds'), d.time().isoformat(), d.microsecond,
             d.timetz().fold, (d + dt.timedelta()).fold, d.utcoffset())
            """));
        Assert.Equal(PyValue.True, Evaluate("""
            a = dt.datetime.fromisoformat('20240229X123456.123456')
            b = a.replace(fold=1)
            a == b and a is not b and dt.time(0) and dt.datetime.fromisoformat('2024-02-29') == dt.datetime(2024, 2, 29)
            """));
    }

    [Fact]
    public void TimedeltaNormalizesAndRoundsToEvenAcrossComponents()
    {
        Assert.Equal(PyValue.From(new PyTuple(-1, 86399, 999999, -0.000001, "-1 day, 23:59:59.999999")), Evaluate("""
            d = dt.timedelta(microseconds=-1)
            (d.days, d.seconds, d.microseconds, d.total_seconds(), str(d))
            """));
        Assert.Equal(PyValue.From(new PyTuple(0, 2, 2, 2, 999999999)), Evaluate("""
            (dt.timedelta(seconds=0.0000005).microseconds,
             dt.timedelta(seconds=0.0000015).microseconds,
             dt.timedelta(microseconds=1, milliseconds=0.0005).microseconds,
             dt.timedelta(microseconds=3, milliseconds=-0.0005).microseconds,
             dt.timedelta.max.days)
            """));
    }

    [Fact]
    public void ArithmeticDistinguishesCalendarDaysFromDatetimeMicroseconds()
    {
        Assert.Equal(PyValue.From(new PyTuple("2024-02-29", "2024-03-01 00:00:00.000001", 2L, -1, 1, 2, 1.5)), Evaluate("""
            us = dt.timedelta.resolution
            (str(dt.date(2024, 3, 1) - dt.timedelta(seconds=-1) * -1 - dt.timedelta(days=1)),
             str(us + dt.datetime(2024, 3, 1)),
             (dt.date(2024, 3, 1) - dt.date(2024, 2, 28)).days,
             -us // (us * 2), (-us % (us * 2)).microseconds,
             ((3 * us) / 2.0).microseconds, (3 * us) / (2 * us))
            """));
        Assert.Equal(PyValue.True, Evaluate("not dt.timedelta() and abs(dt.timedelta.min) == -dt.timedelta.min"));
    }

    [Fact]
    public void DividingLargeDurationsRoundsTheRatioOnlyOnce()
    {
        Assert.Equal(PyValue.From(19.441881239931202), Evaluate("""
            dt.timedelta(microseconds=36662687678365047384) / dt.timedelta(microseconds=1885758236351349411)
            """));
    }

    [Theory]
    [InlineData("dt.date(1900, 2, 29)", "ValueError")]
    [InlineData("dt.date(2024, 1, 1, day=4)", "TypeError")]
    [InlineData("dt.date.fromordinal(0)", "ValueError")]
    [InlineData("dt.date.fromisoformat('2024-03')", "ValueError")]
    [InlineData("dt.time(24)", "ValueError")]
    [InlineData("dt.time(0, 0, 0, 0, None, 1)", "TypeError")]
    [InlineData("dt.time(tzinfo=1)", "TypeError")]
    [InlineData("dt.datetime.fromisoformat('2024-01-01T00:00:00Z')", "ValueError")]
    [InlineData("dt.datetime(2024, 1, 1).isoformat(sep='--')", "TypeError")]
    [InlineData("dt.time().isoformat(timespec='fortnights')", "ValueError")]
    [InlineData("dt.date.min - dt.timedelta(days=1)", "OverflowError")]
    [InlineData("dt.datetime.max + dt.timedelta.resolution", "OverflowError")]
    [InlineData("-dt.timedelta.max", "OverflowError")]
    [InlineData("dt.timedelta.resolution / 0", "ZeroDivisionError")]
    [InlineData("dt.timedelta(1) & dt.timedelta(1)", "TypeError")]
    [InlineData("dt.timedelta(1) + 1", "TypeError")]
    [InlineData("dt.date(2000, 1, 1) < dt.datetime(2000, 1, 1)", "TypeError")]
    public void ReportsPythonErrors(string source, string errorName) =>
        Assert.Equal(errorName, Assert.Throws<PythonException>(() => Evaluate(source)).ErrorName);

    private static PyValue Evaluate(string source)
    {
        var engine = new PythonEngine();
        engine.SetGlobal("dt", PyValue.From(PyDateTimeModule.CreateModule()));
        return engine.Evaluate(source);
    }
}
