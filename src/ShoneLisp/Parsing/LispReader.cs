// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using ShoneJs.Parsing;
using ShoneLisp.Runtime;

namespace ShoneLisp.Parsing;

internal abstract record LispExpression(SourceSpan Span);
internal sealed record AtomExpression(LispValue Value, SourceSpan Span) : LispExpression(Span);
internal sealed record ListExpression(LispExpression[] Items, LispExpression? Tail, SourceSpan Span) : LispExpression(Span);

internal sealed class LispReader(string source, string? fileName, int maximumDepth)
{
    private int _offset;
    private int _line = 1;
    private int _column = 1;
    private bool AtEnd => _offset == source.Length;
    private char Current => source[_offset];
    private SourcePosition Position => new(_offset, _line, _column);

    public ParsedLispScript ReadScript()
    {
        var expressions = new List<LispExpression>();
        SkipTrivia();
        while (!AtEnd)
        {
            expressions.Add(ReadExpression(1));
            SkipTrivia();
        }
        return new(expressions.ToArray(), fileName);
    }

    private LispExpression ReadExpression(int depth)
    {
        SkipTrivia();
        var start = Position;
        if (AtEnd)
            throw Error("Expected an expression.", start);
        if (depth > maximumDepth)
            throw Error("Maximum parse depth exceeded.", start);

        switch (Current)
        {
            case '(':
                Advance();
                return ReadList(start, depth);
            case ')':
                Advance();
                throw Error("Unexpected ')'.", start);
            case '\'':
                Advance();
                var quoteSpan = new SourceSpan(start, Position);
                var datum = ReadExpression(depth + 1);
                return new ListExpression([new AtomExpression(LispValue.FromSymbol("quote"), quoteSpan), datum], null, new(start, Position));
            case '"':
                return ReadString(start);
            default:
                return ReadAtom(start);
        }
    }

    private ListExpression ReadList(SourcePosition start, int depth)
    {
        var items = new List<LispExpression>();
        LispExpression? tail = null;
        SkipTrivia();
        while (!AtEnd && Current != ')')
        {
            if (Current == '.' && (_offset + 1 == source.Length || IsDelimiter(source[_offset + 1])))
            {
                var dot = Position;
                Advance();
                if (items.Count == 0)
                    throw Error("A dotted list needs an item before '.'.", dot);
                tail = ReadExpression(depth + 1);
                SkipTrivia();
                if (AtEnd || Current != ')')
                    throw Error("Expected ')' after the dotted list tail.", Position);
                break;
            }
            items.Add(ReadExpression(depth + 1));
            SkipTrivia();
        }
        if (AtEnd)
            throw Error("Unterminated list; expected ')'.", start);
        Advance();
        return new(items.ToArray(), tail, new(start, Position));
    }

    private AtomExpression ReadString(SourcePosition start)
    {
        Advance();
        var text = new StringBuilder();
        while (!AtEnd)
        {
            var characterStart = Position;
            char ch = Advance();
            if (ch == '"')
                return new(LispValue.FromString(text.ToString()), new(start, Position));
            if (ch == '\\')
            {
                if (AtEnd)
                    break;
                ch = Advance() switch
                {
                    'n' => '\n', 'r' => '\r', 't' => '\t', '"' => '"', '\\' => '\\',
                    _ => throw Error("Unknown string escape.", characterStart),
                };
            }
            text.Append(ch);
        }
        throw Error("Unterminated string.", start);
    }

    private AtomExpression ReadAtom(SourcePosition start)
    {
        while (!AtEnd && !IsDelimiter(Current))
            Advance();
        string atom = source[start.Offset.._offset];
        LispValue value;
        if (atom is "#t" or "#f")
            value = LispValue.FromBoolean(atom == "#t");
        else if (LooksNumeric(atom))
        {
            if (!double.TryParse(atom, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                throw Error($"Invalid or out-of-range number '{atom}'.", start);
            value = LispValue.FromNumber(number);
        }
        else if (atom == "." || atom[0] == '#')
            throw Error($"Unsupported token '{atom}'.", start);
        else
            value = LispValue.FromSymbol(atom);
        return new(value, new(start, Position));
    }

    private static bool LooksNumeric(ReadOnlySpan<char> text)
    {
        if (text[0] is '+' or '-')
            text = text[1..];
        return !text.IsEmpty && (char.IsAsciiDigit(text[0]) || (text[0] == '.' && text.Length > 1 && char.IsAsciiDigit(text[1])));
    }

    private void SkipTrivia()
    {
        while (!AtEnd)
        {
            if (char.IsWhiteSpace(Current))
                Advance();
            else if (Current == ';')
            {
                while (!AtEnd && Current is not ('\r' or '\n'))
                    Advance();
            }
            else
                return;
        }
    }

    private char Advance()
    {
        char ch = source[_offset++];
        if (ch is '\r' or '\n')
        {
            if (ch == '\r' || _offset == 1 || source[_offset - 2] != '\r')
                _line++;
            _column = 1;
        }
        else
            _column++;
        return ch;
    }

    private static bool IsDelimiter(char ch) => char.IsWhiteSpace(ch) || ch is '(' or ')' or '\'' or '"' or ';';
    private LispParseException Error(string message, SourcePosition start) => new(message, new(start, Position), fileName);
}
