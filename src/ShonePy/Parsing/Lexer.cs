// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using ShonePy;
using Sdcb.Arithmetic;

namespace ShonePy.Parsing;

internal enum TokenKind { Name, Number, String, Bytes, Operator, Newline, Indent, Dedent, End }
internal readonly record struct PyToken(TokenKind Kind, string Text, object? Value, int Line, int Column);

internal sealed class Lexer(string source, string? fileName)
{
    private readonly List<PyToken> _tokens = [];
    private readonly Stack<int> _indents = new([0]);
    private int _offset, _line = 1, _column = 1, _depth;

    public List<PyToken> Scan()
    {
        while (_offset < source.Length)
        {
            var lineStart = _column == 1;
            if (lineStart && _depth == 0)
            {
                var start = _offset;
                var width = 0;
                while (Peek() is ' ' or '\t' or '\f')
                {
                    width = Peek() == '\t' ? (width / 8 + 1) * 8 : width + 1;
                    Advance();
                }
                if (Peek() is '\r' or '\n' or '#')
                {
                    while (Peek() == '#') SkipComment();
                    if (Peek() is '\r' or '\n') { Advance(); continue; }
                    continue;
                }
                if (width > _indents.Peek()) { _indents.Push(width); Add(TokenKind.Indent, "", null, _line, 1); }
                else if (width < _indents.Peek())
                {
                    while (width < _indents.Peek()) { _indents.Pop(); Add(TokenKind.Dedent, "", null, _line, 1); }
                    if (width != _indents.Peek()) Error("unindent does not match any outer indentation level", _line, 1);
                }
                if (_offset == start && Peek() is '\r' or '\n') { Advance(); continue; }
            }
            var ch = Peek();
            if (ch is ' ' or '\t' or '\f') { Advance(); continue; }
            if (ch == '#') { SkipComment(); continue; }
            if (ch is '\r' or '\n')
            {
                var line = _line; var col = _column; Advance();
                if (_depth == 0) Add(TokenKind.Newline, "", null, line, col);
                continue;
            }
            var tokenLine = _line; var tokenColumn = _column;
            if (TryScanBytes(tokenLine, tokenColumn)) continue;
            if (char.IsLetter(ch) || ch == '_')
            {
                var begin = _offset;
                do Advance(); while (char.IsLetterOrDigit(Peek()) || Peek() == '_');
                Add(TokenKind.Name, source[begin.._offset], null, tokenLine, tokenColumn); continue;
            }
            if (char.IsAsciiDigit(ch) || ch == '.' && char.IsAsciiDigit(Peek(1)))
            {
                var begin = _offset;
                bool basedInteger = ch == '0' && Peek(1) is 'x' or 'X' or 'o' or 'O' or 'b' or 'B';
                while (char.IsAsciiLetterOrDigit(Peek()) || Peek() is '_' or '.')
                {
                    char digit = Advance();
                    if (!basedInteger && digit is 'e' or 'E' && Peek() is '+' or '-') Advance();
                }
                var text = source[begin.._offset].Replace("_", "", StringComparison.Ordinal);
                object value;
                try
                {
                    value = !basedInteger && (text.Contains('.') || text.Contains('e', StringComparison.OrdinalIgnoreCase))
                        ? double.Parse(text, CultureInfo.InvariantCulture)
                        : ParseInteger(text);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    Error("invalid numeric literal", tokenLine, tokenColumn);
                    throw;
                }
                Add(TokenKind.Number, text, value, tokenLine, tokenColumn); continue;
            }
            if (ch is '\'' or '"') { ScanString(tokenLine, tokenColumn); continue; }
            var foundOperator = Operators.FirstOrDefault(item => source.AsSpan(_offset).StartsWith(item, StringComparison.Ordinal));
            if (foundOperator is null) Error($"invalid syntax: unexpected character '{ch}'", tokenLine, tokenColumn);
            string op = foundOperator!;
            for (var i = 0; i < op.Length; i++) Advance();
            if (op is "(" or "[" or "{") _depth++;
            if (op is ")" or "]" or "}") { _depth--; if (_depth < 0) Error("unmatched closing delimiter", tokenLine, tokenColumn); }
            Add(TokenKind.Operator, op, null, tokenLine, tokenColumn);
        }
        if (_depth != 0) Error("'(' was never closed", _line, _column);
        if (_tokens.Count == 0 || _tokens[^1].Kind != TokenKind.Newline) Add(TokenKind.Newline, "", null, _line, _column);
        while (_indents.Count > 1) { _indents.Pop(); Add(TokenKind.Dedent, "", null, _line, _column); }
        Add(TokenKind.End, "", null, _line, _column);
        return _tokens;
    }

    private static readonly string[] Operators = ["**=", "//=", ">>=", "<<=", "...", "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=", "%=", "@=", "&=", "|=", "^=", "**", "//", ">>", "<<", "->", "(", ")", "[", "]", "{", "}", ":", ",", ".", ";", "+", "-", "*", "/", "%", "@", "&", "|", "^", "~", "<", ">", "=", "+", "-"];

    private static BigInt ParseInteger(string text)
    {
        var radix = 10;
        var offset = 0;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; offset = 2; }
        else if (text.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { radix = 8; offset = 2; }
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { radix = 2; offset = 2; }
        if (offset == text.Length) throw new FormatException();
        BigInt result = 0;
        foreach (var c in text.AsSpan(offset))
        {
            var digit = c is >= '0' and <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10;
            if (digit < 0 || digit >= radix) throw new FormatException();
            result = result * radix + digit;
        }
        return result;
    }

    private bool TryScanBytes(int line, int column)
    {
        char prefix = Peek(), second = Peek(1);
        bool raw = (prefix is 'b' or 'B' && second is 'r' or 'R') ||
                   (prefix is 'r' or 'R' && second is 'b' or 'B');
        if (!raw && prefix is not ('b' or 'B')) return false;
        int prefixLength = raw ? 2 : 1;
        if (Peek(prefixLength) is not ('\'' or '"')) return false;
        for (int i = 0; i < prefixLength; i++) Advance();

        char quote = Advance();
        bool triple = Peek() == quote && Peek(1) == quote;
        if (triple) { Advance(); Advance(); }
        var bytes = new List<byte>();
        while (_offset < source.Length)
        {
            if (Peek() == quote && (!triple || Peek(1) == quote && Peek(2) == quote))
            {
                Advance();
                if (triple) { Advance(); Advance(); }
                Add(TokenKind.Bytes, "", PyBytes.FromOwnedArray(bytes.ToArray()), line, column);
                return true;
            }
            if (!triple && Peek() is '\r' or '\n') break;
            byte value = AdvanceAscii();
            if (value == '\\')
            {
                if (_offset == source.Length) break;
                value = AdvanceAscii();
                if (raw) Append((byte)'\\');
                else
                {
                    if (value == '\n') continue;
                    value = Escape(value);
                }
            }
            Append(value);
        }
        Error("unterminated bytes literal", line, column);
        return false;

        void Append(byte value)
        {
            if (bytes.Count == PyBytes.MaximumLength) Error("bytes literal is too large", line, column);
            bytes.Add(value);
        }

        byte AdvanceAscii()
        {
            char ch = Advance();
            if (ch > 127) Error("bytes can only contain ASCII literal characters", line, column);
            if (ch == '\0') Error("source code string cannot contain null bytes", line, column);
            return (byte)(ch == '\r' ? '\n' : ch);
        }

        byte Escape(byte value)
        {
            if (value == 'x')
            {
                int first = Hex(Peek()), second = Hex(Peek(1));
                if (first < 0 || second < 0) Error("invalid \\x escape", line, column);
                Advance(); Advance();
                return (byte)(first * 16 + second);
            }
            if (value is >= (byte)'0' and <= (byte)'7')
            {
                int octal = value - '0';
                for (int i = 0; i < 2 && Peek() is >= '0' and <= '7'; i++) octal = octal * 8 + Advance() - '0';
                return unchecked((byte)octal);
            }
            int escaped = (char)value switch
            {
                'a' => 7, 'b' => 8, 'f' => 12, 'n' => 10, 'r' => 13, 't' => 9, 'v' => 11,
                '\\' or '\'' or '"' => value, _ => -1
            };
            if (escaped >= 0) return (byte)escaped;
            Append((byte)'\\');
            return value;
        }

        static int Hex(char ch) => ch switch
        {
            >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10,
            >= 'A' and <= 'F' => ch - 'A' + 10, _ => -1
        };
    }

    private void ScanString(int line, int column)
    {
        var quote = Advance();
        var triple = Peek() == quote && Peek(1) == quote;
        if (triple) { Advance(); Advance(); }
        var value = new StringBuilder();
        while (_offset < source.Length)
        {
            if (Peek() == quote && (!triple || Peek(1) == quote && Peek(2) == quote))
            {
                Advance(); if (triple) { Advance(); Advance(); }
                Add(TokenKind.String, value.ToString(), value.ToString(), line, column); return;
            }
            if (!triple && Peek() is '\r' or '\n') Error("unterminated string literal", line, column);
            var ch = Advance();
            if (ch == '\\')
            {
                if (_offset == source.Length) break;
                ch = Advance();
                value.Append(ch switch { 'n' => '\n', 'r' => '\r', 't' => '\t', '\n' => '\0', _ => ch });
            }
            else value.Append(ch);
        }
        Error("unterminated string literal", line, column);
    }

    private void SkipComment() { while (_offset < source.Length && Peek() is not ('\r' or '\n')) Advance(); }
    private char Peek(int n = 0) => _offset + n < source.Length ? source[_offset + n] : '\0';
    private char Advance()
    {
        var c = source[_offset++];
        if (c == '\r') { if (Peek() == '\n') _offset++; _line++; _column = 1; }
        else if (c == '\n') { _line++; _column = 1; }
        else _column++;
        return c;
    }
    private void Add(TokenKind kind, string text, object? value, int line, int column) => _tokens.Add(new(kind, text, value, line, column));
    private void Error(string message, int line, int column) => throw new PythonException("SyntaxError", message, line, column, fileName);
}
