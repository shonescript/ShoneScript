using System.Globalization;
using System.Text;

namespace ShoneJS.Parsing;

public sealed class Lexer(string source)
{
    private int _offset, _line = 1, _column = 1;
    private bool _lineBefore;
    private readonly List<Token> _tokens = [];
    private readonly Stack<int> _templateBraces = new();
    private SourcePosition Position => new(_offset, _line, _column);

    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["let"] = TokenKind.Let, ["const"] = TokenKind.Const, ["var"] = TokenKind.Var,
        ["function"] = TokenKind.Function, ["return"] = TokenKind.Return, ["if"] = TokenKind.If,
        ["else"] = TokenKind.Else, ["while"] = TokenKind.While, ["throw"] = TokenKind.Throw,
        ["true"] = TokenKind.True, ["false"] = TokenKind.False, ["null"] = TokenKind.Null,
        ["this"] = TokenKind.This, ["typeof"] = TokenKind.Typeof, ["void"] = TokenKind.Void,
        ["delete"] = TokenKind.Delete, ["new"] = TokenKind.New, ["try"] = TokenKind.Try,
        ["catch"] = TokenKind.Catch, ["finally"] = TokenKind.Finally, ["instanceof"] = TokenKind.Instanceof,
        ["in"] = TokenKind.In, ["for"] = TokenKind.For, ["switch"] = TokenKind.Switch,
        ["case"] = TokenKind.Case, ["default"] = TokenKind.Default, ["break"] = TokenKind.Break,
        ["continue"] = TokenKind.Continue, ["do"] = TokenKind.Do
    };

    public IReadOnlyList<Token> Scan()
    {
        if (_tokens.Count != 0 && _tokens[^1].Kind == TokenKind.End) return _tokens;
        if (Peek() == '#' && Peek(1) == '!') SkipLine();
        while (_offset < source.Length)
        {
            if (SkipTrivia()) continue;
            var start = Position;
            var ch = Advance();
            if (IsIdentifierStart(ch))
            {
                while (IsIdentifierPart(Peek())) Advance();
                var text = source[start.Offset.._offset];
                Add(Keywords.GetValueOrDefault(text, TokenKind.Identifier), start, text);
                continue;
            }
            if (char.IsAsciiDigit(ch) || ch == '.' && char.IsAsciiDigit(Peek()))
            {
                ScanNumber(start, ch);
                continue;
            }
            if (ch is '\'' or '"')
            {
                ScanString(start, ch);
                continue;
            }
            if (ch == '`')
            {
                ScanTemplate(start, first: true);
                continue;
            }
            if (_templateBraces.Count > 0 && ch is '{' or '}')
            {
                var braces = _templateBraces.Pop();
                if (braces == 0 && ch == '}')
                {
                    ScanTemplate(start, first: false);
                    continue;
                }
                _templateBraces.Push(braces + (ch == '{' ? 1 : -1));
            }
            Add(ch switch
            {
                '(' => TokenKind.LeftParen, ')' => TokenKind.RightParen,
                '{' => TokenKind.LeftBrace, '}' => TokenKind.RightBrace,
                '[' => TokenKind.LeftBracket, ']' => TokenKind.RightBracket,
                ';' => TokenKind.Semicolon, ',' => TokenKind.Comma, '.' => TokenKind.Dot,
                ':' => TokenKind.Colon, '~' => TokenKind.Tilde,
                '?' when Peek() == '.' && !char.IsAsciiDigit(Peek(1)) => AdvanceOptionalChain(),
                '?' => Match('?') ? Match('=') ? TokenKind.NullishAssign : TokenKind.NullishCoalescing : TokenKind.Question,
                '+' => Match('+') ? TokenKind.PlusPlus : Match('=') ? TokenKind.PlusEqual : TokenKind.Plus,
                '-' => Match('-') ? TokenKind.MinusMinus : Match('=') ? TokenKind.MinusEqual : TokenKind.Minus,
                '*' => Match('*') ? Match('=') ? TokenKind.PowerEqual : TokenKind.Power : Match('=') ? TokenKind.StarEqual : TokenKind.Star,
                '/' => Match('=') ? TokenKind.SlashEqual : TokenKind.Slash,
                '%' => Match('=') ? TokenKind.PercentEqual : TokenKind.Percent,
                '!' => Match('=') ? Match('=') ? TokenKind.StrictNotEqual : TokenKind.NotEqual : TokenKind.Bang,
                '=' => Match('>') ? TokenKind.Arrow : Match('=') ? Match('=') ? TokenKind.StrictEqual : TokenKind.EqualEqual : TokenKind.Equal,
                '<' => Match('<') ? Match('=') ? TokenKind.ShiftLeftEqual : TokenKind.ShiftLeft : Match('=') ? TokenKind.LessEqual : TokenKind.Less,
                '>' => ScanGreaterThan(),
                '&' => Match('&') ? Match('=') ? TokenKind.AndAndEqual : TokenKind.AndAnd : Match('=') ? TokenKind.BitwiseAndEqual : TokenKind.BitwiseAnd,
                '|' => Match('|') ? Match('=') ? TokenKind.OrOrEqual : TokenKind.OrOr : Match('=') ? TokenKind.BitwiseOrEqual : TokenKind.BitwiseOr,
                '^' => Match('=') ? TokenKind.BitwiseXorEqual : TokenKind.BitwiseXor,
                _ => throw Error($"Unexpected character '{ch}'", start)
            }, start);
        }
        if (_templateBraces.Count != 0) throw Error("Unterminated template substitution", Position);
        Add(TokenKind.End, Position);
        return _tokens;
    }

    private bool SkipTrivia()
    {
        if (IsLineTerminator(Peek()) || IsWhiteSpace(Peek()))
        {
            _lineBefore |= IsLineTerminator(Advance());
            return true;
        }
        if (Peek() != '/') return false;
        if (Peek(1) == '/')
        {
            SkipLine();
            return true;
        }
        if (Peek(1) != '*') return false;
        var start = Position;
        Advance();
        Advance();
        while (!(Peek() == '*' && Peek(1) == '/'))
        {
            if (_offset == source.Length) throw Error("Unterminated comment", start);
            _lineBefore |= IsLineTerminator(Advance());
        }
        Advance();
        Advance();
        return true;
    }

    private void SkipLine()
    {
        while (_offset < source.Length && !IsLineTerminator(Peek())) Advance();
    }

    private void ScanNumber(SourcePosition start, char first)
    {
        if (first == '0' && Peek() is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
        {
            var radix = char.ToLowerInvariant(Advance()) switch { 'x' => 16, 'o' => 8, _ => 2 };
            if (!ScanDigits(radix)) throw Error("Expected digits in numeric literal", Position);
            var suffix = Match('n');
            ValidateNumericEnd();
            Add(suffix ? TokenKind.BigInt : TokenKind.Number, start);
            return;
        }
        ScanDigits(10, precedingDigit: first != '.');
        var fractional = first == '.';
        if (!fractional && Match('.'))
        {
            fractional = true;
            ScanDigits(10);
        }
        if (Peek() is 'e' or 'E')
        {
            fractional = true;
            Advance();
            if (Peek() is '+' or '-') Advance();
            if (!ScanDigits(10)) throw Error("Expected exponent digits", Position);
        }
        var bigint = !fractional && Match('n');
        if (bigint && first == '0' && _offset - start.Offset > 2)
            throw Error("Invalid BigInt literal", start);
        if (first == '0' && start.Offset + 1 < _offset &&
            (char.IsAsciiDigit(source[start.Offset + 1]) || source[start.Offset + 1] == '_') &&
            source.AsSpan(start.Offset, _offset - start.Offset).Contains('_'))
            throw Error("Invalid numeric separator", start);
        ValidateNumericEnd();
        Add(bigint ? TokenKind.BigInt : TokenKind.Number, start);
    }

    private bool ScanDigits(int radix, bool precedingDigit = false)
    {
        var sawDigit = precedingDigit;
        while (IsDigit(Peek(), radix) || Peek() == '_')
        {
            if (Peek() == '_' && (!sawDigit || !IsDigit(Peek(1), radix)))
                throw Error("Invalid numeric separator", Position);
            sawDigit = true;
            Advance();
        }
        return sawDigit;
    }

    private void ValidateNumericEnd()
    {
        if (IsIdentifierPart(Peek())) throw Error("Invalid numeric literal suffix", Position);
    }

    private void ScanString(SourcePosition start, char quote)
    {
        var cooked = new StringBuilder();
        var legacy = false;
        while (_offset < source.Length && Peek() != quote)
        {
            if (IsLineTerminator(Peek())) throw Error("Unterminated string", start);
            var characterStart = Position;
            var ch = Advance();
            if (ch == '\\') legacy |= ScanEscape(cooked, characterStart, template: false);
            else cooked.Append(ch);
        }
        if (!Match(quote)) throw Error("Unterminated string", start);
        Add(TokenKind.String, start, cooked.ToString(), legacy: legacy);
    }

    private void ScanTemplate(SourcePosition start, bool first)
    {
        var cooked = new StringBuilder();
        var rawStart = _offset;
        while (_offset < source.Length)
        {
            var characterStart = Position;
            var ch = Advance();
            if (ch == '`')
            {
                Add(first ? TokenKind.Template : TokenKind.TemplateTail, start, cooked.ToString(),
                    TemplateRaw(rawStart, characterStart.Offset));
                return;
            }
            if (ch == '$' && Match('{'))
            {
                Add(first ? TokenKind.TemplateHead : TokenKind.TemplateMiddle, start, cooked.ToString(),
                    TemplateRaw(rawStart, characterStart.Offset));
                _templateBraces.Push(0);
                return;
            }
            if (ch == '\\') ScanEscape(cooked, characterStart, template: true);
            else cooked.Append(ch == '\r' ? '\n' : ch);
        }
        throw Error("Unterminated template", start);
    }

    private string TemplateRaw(int start, int end) =>
        source[start..end].Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private bool ScanEscape(StringBuilder cooked, SourcePosition start, bool template)
    {
        if (_offset == source.Length) return false;
        var escaped = Advance();
        if (IsLineTerminator(escaped)) return false;
        if (escaped == 'x') cooked.Append((char)ScanHex(2, start));
        else if (escaped == 'u')
        {
            if (!Match('{')) cooked.Append((char)ScanHex(4, start));
            else
            {
                var point = 0;
                var digits = 0;
                while (IsDigit(Peek(), 16))
                {
                    point = point * 16 + HexValue(Advance());
                    if (point > 0x10FFFF) throw Error("Invalid Unicode code point", start);
                    digits++;
                }
                if (digits == 0 || !Match('}')) throw Error("Invalid Unicode escape", start);
                if (point <= char.MaxValue) cooked.Append((char)point);
                else cooked.Append(char.ConvertFromUtf32(point));
            }
        }
        else if (escaped == '0' && !char.IsAsciiDigit(Peek())) cooked.Append('\0');
        else if (char.IsAsciiDigit(escaped))
        {
            if (template) throw Error("Invalid numeric escape in template", start);
            if (escaped is '8' or '9') cooked.Append(escaped);
            else
            {
                var value = escaped - '0';
                var remaining = escaped <= '3' ? 2 : 1;
                while (remaining-- > 0 && IsDigit(Peek(), 8)) value = value * 8 + Advance() - '0';
                cooked.Append((char)value);
            }
            return true;
        }
        else cooked.Append(escaped switch
        {
            'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => escaped
        });
        return false;
    }

    private int ScanHex(int count, SourcePosition start)
    {
        var value = 0;
        while (count-- > 0)
        {
            if (!IsDigit(Peek(), 16)) throw Error("Invalid hexadecimal escape", start);
            value = value * 16 + HexValue(Advance());
        }
        return value;
    }

    private char Peek(int distance = 0) => _offset + distance < source.Length ? source[_offset + distance] : '\0';

    private char Advance()
    {
        var ch = source[_offset++];
        if (IsLineTerminator(ch))
        {
            if (ch == '\r' && Peek() == '\n') _offset++;
            _line++;
            _column = 1;
        }
        else _column++;
        return ch;
    }

    private TokenKind ScanGreaterThan()
    {
        if (!Match('>')) return Match('=') ? TokenKind.GreaterEqual : TokenKind.Greater;
        if (Match('>')) return Match('=') ? TokenKind.ShiftUnsignedRightEqual : TokenKind.ShiftUnsignedRight;
        return Match('=') ? TokenKind.ShiftRightEqual : TokenKind.ShiftRight;
    }

    private TokenKind AdvanceOptionalChain()
    {
        Advance();
        return TokenKind.OptionalChain;
    }

    private bool Match(char ch)
    {
        if (Peek() != ch) return false;
        Advance();
        return true;
    }

    private void Add(TokenKind kind, SourcePosition start, string? text = null, string? raw = null, bool legacy = false)
    {
        raw ??= source[start.Offset.._offset];
        _tokens.Add(new Token(kind, text ?? raw, start.Offset, start.Line, start.Column)
        {
            Raw = raw, End = Position, HasLineTerminatorBefore = _lineBefore, HasLegacyOctalEscape = legacy
        });
        _lineBefore = false;
    }

    private static bool IsWhiteSpace(char ch) => ch is '\t' or '\v' or '\f' or '\uFEFF' ||
        char.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator;
    private static bool IsLineTerminator(char ch) => ch is '\r' or '\n' or '\u2028' or '\u2029';
    private static bool IsIdentifierStart(char ch) => ch is '_' or '$' || char.IsLetter(ch) ||
        char.GetUnicodeCategory(ch) == UnicodeCategory.LetterNumber;
    private static bool IsIdentifierPart(char ch) => IsIdentifierStart(ch) || ch is '\u200C' or '\u200D' ||
        char.GetUnicodeCategory(ch) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation
            or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    private static bool IsDigit(char ch, int radix) => HexValue(ch) < radix;
    private static int HexValue(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0', >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10, _ => 16
    };
    private static JsSyntaxException Error(string message, SourcePosition position) => new(message, position.Line, position.Column)
    {
        Offset = position.Offset
    };
}
