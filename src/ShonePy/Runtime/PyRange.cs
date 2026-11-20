// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections;
using Sdcb.Arithmetic;

namespace ShonePy;

/// <summary>An immutable arithmetic sequence of Python integers.</summary>
public sealed class PyRange : IEquatable<PyRange>, IEnumerable<PyValue>
{
    public BigInt Start { get; }
    public BigInt Stop { get; }
    public BigInt Step { get; }
    public BigInt Length { get; }

    public PyRange(BigInt start, BigInt stop, BigInt step)
    {
        if (step.IsZero) throw new PythonException("ValueError", "range() arg 3 must not be zero");
        Start = start;
        Stop = stop;
        Step = step;
        var distance = step.Sign > 0 ? stop - start : start - stop;
        Length = distance.Sign <= 0 ? BigInt.Zero : (distance - BigInt.One) / BigInt.Abs(step) + BigInt.One;
    }

    internal PyValue GetItem(PyValue value)
    {
        BigInt index = value.Type switch
        {
            PyType.Integer => (BigInt)value.Data!,
            PyType.Boolean => value.IsTruthy() ? BigInt.One : BigInt.Zero,
            _ => throw new PythonException("TypeError", $"range indices must be integers or slices, not {PyOperations.TypeName(value)}")
        };
        if (index.Sign < 0) index += Length;
        if (index.Sign < 0 || index >= Length) throw new PythonException("IndexError", "range object index out of range");
        return PyValue.From(ValueAt(index));
    }

    internal PyRange GetSlice(BigInt start, BigInt stop, BigInt step) =>
        new(ValueAt(start), ValueAt(stop), Step * step);

    private BigInt ValueAt(BigInt index) => Start + index * Step;

    internal bool Contains(PyValue value) => TryIndexOf(value, out _);

    private bool TryIndexOf(PyValue value, out BigInt index)
    {
        index = BigInt.Zero;
        BigInt candidate;
        switch (value.Type)
        {
            case PyType.Integer:
                candidate = (BigInt)value.Data!;
                break;
            case PyType.Boolean:
                candidate = value.IsTruthy() ? BigInt.One : BigInt.Zero;
                break;
            // An integral float can compare equal to a range element. All other
            // supported non-integer values compare unequal to Python integers.
            case PyType.Float when value.Data is double number && double.IsFinite(number) && number == Math.Truncate(number):
                candidate = new BigInt(number);
                break;
            default:
                return false;
        }

        if (Step.Sign > 0 ? candidate < Start || candidate >= Stop : candidate > Start || candidate <= Stop)
            return false;
        var offset = candidate - Start;
        if (!(offset % Step).IsZero) return false;
        index = offset / Step;
        return true;
    }

    internal PyValue GetAttribute(string name) => name switch
    {
        "start" => PyValue.From(Start),
        "stop" => PyValue.From(Stop),
        "step" => PyValue.From(Step),
        "count" => PyValue.From(new PyCallable(args =>
        {
            CheckMethodArity(name, args.Count);
            return PyValue.From(Contains(args[0]) ? 1 : 0);
        }, name)),
        "index" => PyValue.From(new PyCallable(args =>
        {
            CheckMethodArity(name, args.Count);
            return TryIndexOf(args[0], out BigInt index) ? PyValue.From(index)
                : throw new PythonException("ValueError", "sequence.index(x): x not in sequence");
        }, name)),
        _ => throw new PythonException("AttributeError", $"'range' object has no attribute '{name}'")
    };

    private static void CheckMethodArity(string name, int count)
    {
        if (count != 1)
            throw new PythonException("TypeError", $"range.{name}() takes exactly one argument ({count} given)");
    }

    internal PyIterator GetIterator()
    {
        var current = Start;
        bool ascending = Step.Sign > 0;
        return new PyIterator("range_iterator", () =>
        {
            if (ascending ? current >= Stop : current <= Stop) return null;
            var value = PyValue.From(current);
            current += Step;
            return value;
        });
    }

    public IEnumerator<PyValue> GetEnumerator() => GetIterator().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(PyRange? other) => other is not null && Length == other.Length &&
        (Length.IsZero || (Start == other.Start && (Length == BigInt.One || Step == other.Step)));

    public override bool Equals(object? obj) => obj is PyRange other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Length, Length.IsZero ? BigInt.Zero : Start,
        Length <= BigInt.One ? BigInt.Zero : Step);

    public override string ToString() => Step == BigInt.One ? $"range({Start}, {Stop})" : $"range({Start}, {Stop}, {Step})";
}
