// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Collections;
using System.Globalization;
using System.Text;
using Sdcb.Arithmetic;

namespace ShonePy;

public sealed class PyBytes : IReadOnlyList<byte>, IEquatable<PyBytes>
{
    internal const int MaximumLength = 8_000_000;
    internal static readonly PyBytes Empty = new([]);
    private readonly byte[] _data;

    private PyBytes(byte[] data) => _data = data;
    internal static PyBytes FromOwnedArray(byte[] data) => data.Length == 0 ? Empty : new(data);
    public int Count => _data.Length;
    public byte this[int index] => _data[index];
    public IEnumerator<byte> GetEnumerator() => ((IEnumerable<byte>)_data).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(PyBytes? other) => ReferenceEquals(this, other) ||
        (other is not null && _data.AsSpan().SequenceEqual(other._data));
    public override bool Equals(object? obj) => obj is PyBytes bytes && Equals(bytes);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_data);
        return hash.ToHashCode();
    }

    internal int CompareTo(PyBytes other) => _data.AsSpan().SequenceCompareTo(other._data);
    internal bool Contains(PyBytes other) => _data.AsSpan().IndexOf(other._data) >= 0;
    internal bool Contains(byte value) => _data.AsSpan().IndexOf(value) >= 0;

    internal PyBytes Slice(int first, int stride, int count)
    {
        if (count == 0) return Empty;
        if (stride == 1) return count == Count ? this : new(_data.AsSpan(first, count).ToArray());
        var data = new byte[count];
        for (int i = 0; i < count; i++) data[i] = _data[first + i * stride];
        return new(data);
    }

    internal PyBytes Concat(PyBytes other)
    {
        if (Count == 0) return other;
        if (other.Count == 0) return this;
        int length = CheckLength((long)Count + other.Count);
        var data = new byte[length];
        _data.CopyTo(data, 0);
        other._data.CopyTo(data, Count);
        return new(data);
    }

    internal PyBytes Repeat(BigInt count)
    {
        if (count > new BigInt((long)nint.MaxValue) || count < new BigInt((long)nint.MinValue))
            throw new PythonException("OverflowError", "repeated bytes count is too large");
        if (count.Sign <= 0 || Count == 0) return Empty;
        if (count == BigInt.One) return this;
        if (count > new BigInt(MaximumLength / Count)) throw TooLarge();
        var data = new byte[(int)count * Count];
        _data.CopyTo(data, 0);
        // Double the initialized prefix on each copy, including one-byte sources.
        for (int copied = Count; copied < data.Length;)
        {
            int chunk = Math.Min(copied, data.Length - copied);
            data.AsSpan(0, chunk).CopyTo(data.AsSpan(copied));
            copied += chunk;
        }
        return new(data);
    }

    internal static int CheckLength(long length) => length <= MaximumLength ? (int)length : throw TooLarge();
    internal static PythonException TooLarge() => new("OverflowError", "bytes result exceeds the interpreter size limit");

    public override string ToString()
    {
        char quote = Contains((byte)'\'') && !Contains((byte)'"') ? '"' : '\'';
        var text = new StringBuilder(Count + 3).Append('b').Append(quote);
        foreach (byte value in _data)
        {
            char ch = (char)value;
            if (ch == quote || ch == '\\') text.Append('\\').Append(ch);
            else if (ch == '\t') text.Append("\\t");
            else if (ch == '\n') text.Append("\\n");
            else if (ch == '\r') text.Append("\\r");
            else if (value is >= 32 and < 127) text.Append(ch);
            else text.Append("\\x").Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }
        return text.Append(quote).ToString();
    }
}
