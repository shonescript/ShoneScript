// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Text;
using Sdcb.Arithmetic;

namespace ShonePy;

internal static class PyBytesConstructor
{
    internal static PyCallable Callable { get; } = new(args => Construct(args, null), "bytes")
    {
        InvokeWithKeywords = Construct
    };

    private static PyValue Construct(IReadOnlyList<PyValue> args, IReadOnlyDictionary<string, PyValue>? keywords)
    {
        if (args.Count > 3) throw new PythonException("TypeError", "bytes() takes at most 3 arguments");
        var parameters = new PyValue?[3];
        for (int i = 0; i < args.Count; i++) parameters[i] = args[i];
        if (keywords is not null)
        {
            foreach (var (name, value) in keywords)
            {
                int index = name switch
                {
                    "source" => 0, "encoding" => 1, "errors" => 2,
                    _ => throw new PythonException("TypeError", $"'{name}' is an invalid keyword argument for bytes()")
                };
                if (parameters[index] is not null)
                    throw new PythonException("TypeError", $"bytes() got multiple values for argument '{name}'");
                parameters[index] = value;
            }
        }

        string? encoding = OptionalString(parameters[1], "encoding");
        string? errors = OptionalString(parameters[2], "errors");
        if (parameters[0] is { Data: string text })
        {
            if (encoding is null) throw new PythonException("TypeError", "string argument without an encoding");
            return PyValue.From(Encode(text, encoding, errors ?? "strict"));
        }
        if (encoding is not null || errors is not null)
            throw new PythonException("TypeError", "encoding or errors without a string argument");
        if (parameters[0] is not { } source) return PyValue.From(PyBytes.Empty);
        if (source.Type == PyType.Bytes) return source;
        if (source.Type is PyType.Integer or PyType.Boolean)
        {
            BigInt count = source.Type == PyType.Integer ? (BigInt)source.Data! : source.IsTruthy() ? BigInt.One : BigInt.Zero;
            if (count.Sign < 0) throw new PythonException("ValueError", "negative count");
            if (count > new BigInt(PyBytes.MaximumLength)) throw PyBytes.TooLarge();
            return PyValue.From(PyBytes.FromOwnedArray(new byte[(int)count]));
        }

        var bytes = new List<byte>();
        // A PyIterator charges each pull even for lists and tuples.
        foreach (var item in PyOperations.GetIterator(source))
        {
            if (bytes.Count == PyBytes.MaximumLength) throw PyBytes.TooLarge();
            bytes.Add(PyOperations.ByteArgument(item));
        }
        return PyValue.From(PyBytes.FromOwnedArray(bytes.ToArray()));
    }

    private static string? OptionalString(PyValue? value, string name) => value switch
    {
        null => null,
        { Data: string text } => text,
        _ => throw new PythonException("TypeError", $"bytes() argument '{name}' must be str")
    };

    private static PyBytes Encode(string text, string name, string errors)
    {
        string normalized = name.Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
        int limit = normalized switch
        {
            "utf-8" or "utf8" or "u8" or "utf" => 0,
            "ascii" or "us-ascii" or "646" => 127,
            "latin-1" or "latin1" or "latin" or "iso-8859-1" or "iso8859-1" or "l1" or "cp819" => 255,
            _ => throw new PythonException("LookupError", $"unknown encoding: {name}")
        };

        if (limit == 0)
        {
            var encoding = (Encoding)new UTF8Encoding(false, true).Clone();
            if (errors == "ignore") encoding.EncoderFallback = new EncoderReplacementFallback("");
            if (errors == "replace") encoding.EncoderFallback = new EncoderReplacementFallback("?");
            try
            {
                PyBytes.CheckLength(encoding.GetByteCount(text));
                return PyBytes.FromOwnedArray(encoding.GetBytes(text));
            }
            catch (EncoderFallbackException) { throw EncodingError(name, errors); }
        }

        var bytes = new List<byte>();
        foreach (Rune rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            if (value > limit)
            {
                if (errors == "ignore") continue;
                if (errors != "replace") throw EncodingError(name, errors);
                value = '?';
            }
            if (bytes.Count == PyBytes.MaximumLength) throw PyBytes.TooLarge();
            bytes.Add((byte)value);
        }
        return PyBytes.FromOwnedArray(bytes.ToArray());
    }

    private static PythonException EncodingError(string name, string errors) => errors == "strict"
        ? new("UnicodeEncodeError", $"'{name}' codec cannot encode the string")
        : new("LookupError", $"unknown error handler name '{errors}'");
}
