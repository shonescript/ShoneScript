// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;
using System.Text;
using ShoneJs.Parsing;

namespace ShoneLua.Parsing;

internal sealed class LuaLexer(string source, string? fileName)
{
    private int _offset;
    private int _line = 1;
    private int _column = 1;
    private bool End => _offset == source.Length;
    private char Current => At(_offset);
    private char At(int offset) => offset < source.Length ? source[offset] : '\0';
    private SourcePosition Position => new(_offset, _line, _column);

    public Token Next()
    {
        SkipTrivia();
        var start = Position;
        if (End) return Make(Kind.Eof, start);
        if (NameStart(Current))
        {
            Advance();
            while (NamePart(Current)) Advance();
            var name = source[start.Offset.._offset];
            return Make(Keyword(name), start, name);
        }
        if (char.IsAsciiDigit(Current) || (Current == '.' && char.IsAsciiDigit(At(_offset + 1))))
            return Number(start);
        if (Current is '\'' or '"') return ShortString(start);
        var level = LongDelimiter(_offset, '[');
        if (level >= 0) return Make(Kind.String, start, LongString(start, level, comment: false));

        var ch = Current;
        Advance();
        return Make(ch switch
        {
            '+' => Kind.Plus, '-' => Kind.Minus, '*' => Kind.Star, '%' => Kind.Percent,
            '^' => Kind.Power, '#' => Kind.Length, '(' => Kind.LeftParen, ')' => Kind.RightParen,
            '[' => Kind.LeftBracket, ']' => Kind.RightBracket, '{' => Kind.LeftBrace, '}' => Kind.RightBrace,
            ',' => Kind.Comma, ';' => Kind.Semicolon, '/' => Take('/') ? Kind.FloorDiv : Kind.Slash,
            '=' => Take('=') ? Kind.EqualEqual : Kind.Equal, '~' when Take('=') => Kind.NotEqual,
            '<' => Take('=') ? Kind.LessEqual : Kind.Less, '>' => Take('=') ? Kind.GreaterEqual : Kind.Greater,
            '.' => Take('.') ? Kind.Concat : Kind.Dot,
            _ => throw Error($"Unexpected character '{ch}'.", start)
        }, start);
    }

    private void SkipTrivia()
    {
        while (!End)
        {
            if (Space(Current)) { Advance(); continue; }
            if (Current != '-' || At(_offset + 1) != '-') break;
            var start = Position;
            Advance();
            Advance();
            var level = LongDelimiter(_offset, '[');
            if (level >= 0) LongString(start, level, comment: true);
            else while (!End && !Newline(Current)) Advance();
        }
    }

    private Token Number(SourcePosition start)
    {
        var radix = 10;
        if (Current == '0' && At(_offset + 1) is 'x' or 'X')
        {
            Advance();
            Advance();
            radix = 16;
        }
        var digitStart = _offset;
        while (Digit(Current, radix) >= 0) Advance();
        var hasDigits = _offset != digitStart;
        if (Take('.'))
        {
            digitStart = _offset;
            while (Digit(Current, radix) >= 0) Advance();
            hasDigits |= _offset != digitStart;
        }
        if (radix == 16 ? Current is 'p' or 'P' : Current is 'e' or 'E')
        {
            Advance();
            if (Current is '+' or '-') Advance();
            digitStart = _offset;
            while (char.IsAsciiDigit(Current)) Advance();
            if (_offset == digitStart) throw Error("Malformed number exponent.", start);
        }
        if (!hasDigits || NamePart(Current) || Current == '.') throw Error("Malformed number.", start);
        return Make(Kind.Number, start, source[start.Offset.._offset]);
    }

    internal static bool TryNumber(string text, out double number)
    {
        var digits = text.AsSpan().Trim(" \t\n\r\v\f");
        var negative = !digits.IsEmpty && digits[0] == '-';
        if (!digits.IsEmpty && digits[0] is '+' or '-') digits = digits[1..];
        if (!digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            number = 0;
            if (digits.IsEmpty || digits.Contains('\0') || (!char.IsAsciiDigit(digits[0]) && digits[0] != '.') ||
                !double.TryParse(digits, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out number))
                return false;
            if (negative) number = -number;
            return true;
        }

        ulong mantissa = 0;
        var sticky = false;
        var seenDigit = false;
        var seenPoint = false;
        var keptDigits = 0;
        long exponent = 0;
        var i = 2;
        for (; i < digits.Length; i++)
        {
            var ch = digits[i];
            if (ch is 'p' or 'P') break;
            if (ch == '.' && !seenPoint) { seenPoint = true; continue; }
            var digit = Digit(ch, 16);
            if (digit < 0) { number = 0; return false; }
            seenDigit = true;
            if (seenPoint) exponent -= 4;
            if (keptDigits >= 16)
            {
                exponent += 4;
                sticky |= digit != 0;
            }
            else if (keptDigits != 0 || digit != 0)
            {
                mantissa = (mantissa << 4) | (uint)digit;
                keptDigits++;
            }
        }
        if (i < digits.Length)
        {
            var exponentDigits = digits[(i + 1)..];
            var negativeExponent = !exponentDigits.IsEmpty && exponentDigits[0] == '-';
            if (!exponentDigits.IsEmpty && exponentDigits[0] is '+' or '-') exponentDigits = exponentDigits[1..];
            if (exponentDigits.IsEmpty) { number = 0; return false; }
            long magnitude = 0;
            foreach (var ch in exponentDigits)
            {
                if (!char.IsAsciiDigit(ch)) { number = 0; return false; }
                magnitude = Math.Min(magnitude * 10 + ch - '0', 10_000_000_000L);
            }
            exponent += negativeExponent ? -magnitude : magnitude;
        }
        number = ScaleHex(mantissa, exponent, sticky);
        if (negative) number = -number;
        return seenDigit;
    }

    private static double ScaleHex(ulong mantissa, long exponent, bool sticky)
    {
        if (mantissa == 0) return 0;
        // Round once, to nearest even, including the reduced precision of subnormals.
        var discard = Math.Max(BitOperations.Log2(mantissa) - 52, -1074 - exponent);
        if (discard > 64) return 0;
        if (discard > 0)
        {
            var halfway = 1UL << ((int)discard - 1);
            var remainder = mantissa & (halfway | (halfway - 1));
            mantissa = discard == 64 ? 0 : mantissa >> (int)discard;
            if (remainder > halfway || (remainder == halfway && (sticky || (mantissa & 1) != 0))) mantissa++;
            exponent += discard;
        }
        return Math.ScaleB(mantissa, (int)Math.Min(exponent, 2048));
    }

    private Token ShortString(SourcePosition start)
    {
        var quote = Current;
        Advance();
        var text = new StringBuilder();
        while (!End && Current != quote)
        {
            if (Newline(Current)) throw Error("Unterminated string.", start);
            if (!Take('\\')) { text.Append(Current); Advance(); continue; }
            if (End) break;
            var escape = Current;
            if (char.IsAsciiDigit(escape))
            {
                var value = 0;
                for (var n = 0; n < 3 && char.IsAsciiDigit(Current); n++)
                {
                    value = value * 10 + Current - '0';
                    Advance();
                }
                if (value > 255) throw Error("Decimal escape is greater than 255.", start);
                text.Append((char)value);
                continue;
            }
            Advance();
            switch (escape)
            {
                case 'a': text.Append('\a'); break;
                case 'b': text.Append('\b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': case '\n': case '\r': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'v': text.Append('\v'); break;
                case '\\': case '\'': case '"': text.Append(escape); break;
                case 'z': while (Space(Current)) Advance(); break;
                case 'x': text.Append((char)(ReadHex(start) * 16 + ReadHex(start))); break;
                case 'u':
                    if (!Take('{') || Digit(Current, 16) < 0) throw Error("Expected a Unicode escape of the form \\u{...}.", start);
                    var scalar = 0;
                    while (Digit(Current, 16) >= 0)
                    {
                        scalar = scalar * 16 + ReadHex(start);
                        if (scalar > 0x10ffff) throw Error("Unicode escape is outside the supported scalar range.", start);
                    }
                    if (!Take('}') || scalar is >= 0xd800 and <= 0xdfff) throw Error("Invalid Unicode escape.", start);
                    text.Append(char.ConvertFromUtf32(scalar));
                    break;
                default: throw Error($"Invalid escape sequence '\\{escape}'.", start);
            }
        }
        if (!Take(quote)) throw Error("Unterminated string.", start);
        return Make(Kind.String, start, text.ToString());
    }

    private string LongString(SourcePosition start, int level, bool comment)
    {
        for (var i = 0; i < level + 2; i++) Advance();
        if (Newline(Current)) Advance();
        StringBuilder? text = comment ? null : new();
        while (!End)
        {
            if (Current == ']' && LongDelimiter(_offset, ']') == level)
            {
                for (var i = 0; i < level + 2; i++) Advance();
                return text?.ToString() ?? "";
            }
            text?.Append(Newline(Current) ? '\n' : Current);
            Advance();
        }
        throw Error(comment ? "Unterminated long comment." : "Unterminated long string.", start);
    }

    private int LongDelimiter(int offset, char bracket)
    {
        if (At(offset) != bracket) return -1;
        var cursor = offset + 1;
        while (At(cursor) == '=') cursor++;
        return At(cursor) == bracket ? cursor - offset - 1 : -1;
    }

    private int ReadHex(SourcePosition start)
    {
        var digit = Digit(Current, 16);
        if (digit < 0) throw Error("Expected a hexadecimal digit in escape sequence.", start);
        Advance();
        return digit;
    }

    private bool Take(char ch)
    {
        if (Current != ch || End) return false;
        Advance();
        return true;
    }

    private void Advance()
    {
        var current = source[_offset++];
        if (Newline(current))
        {
            if (Newline(Current) && Current != current) _offset++;
            _line++;
            _column = 1;
        }
        else _column++;
    }

    private Token Make(Kind kind, SourcePosition start, string text = "") => new(kind, text, new SourceSpan(start, Position));
    private LuaParseException Error(string message, SourcePosition start) => new(message, new SourceSpan(start, Position), fileName);
    private static bool Newline(char ch) => ch is '\r' or '\n';
    private static bool Space(char ch) => ch is ' ' or '\t' or '\v' or '\f' or '\r' or '\n';
    private static bool NameStart(char ch) => char.IsAsciiLetter(ch) || ch == '_';
    private static bool NamePart(char ch) => NameStart(ch) || char.IsAsciiDigit(ch);
    private static int Digit(char ch, int radix) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' when radix == 16 => ch - 'a' + 10,
        >= 'A' and <= 'F' when radix == 16 => ch - 'A' + 10,
        _ => -1
    };

    private static Kind Keyword(string name) => name switch
    {
        "and" => Kind.And, "break" => Kind.Break, "do" => Kind.Do, "else" => Kind.Else, "elseif" => Kind.ElseIf,
        "end" => Kind.End, "false" => Kind.False, "for" => Kind.For, "function" => Kind.Function,
        "if" => Kind.If, "in" => Kind.In,
        "local" => Kind.Local, "nil" => Kind.Nil, "not" => Kind.Not, "or" => Kind.Or, "repeat" => Kind.Repeat,
        "return" => Kind.Return, "then" => Kind.Then, "true" => Kind.True, "until" => Kind.Until,
        "while" => Kind.While, "goto" => Kind.Goto,
        _ => Kind.Name
    };
}
