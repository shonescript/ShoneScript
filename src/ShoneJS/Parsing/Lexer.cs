namespace ShoneJS.Parsing;

public sealed class Lexer(string source)
{
    private int _i, _line = 1, _column = 1;
    private readonly List<Token> _tokens = [];
    private static readonly Dictionary<string, TokenKind> Keywords = new() { ["let"] = TokenKind.Let, ["const"] = TokenKind.Const, ["var"] = TokenKind.Var, ["function"] = TokenKind.Function, ["return"] = TokenKind.Return, ["if"] = TokenKind.If, ["else"] = TokenKind.Else, ["while"] = TokenKind.While, ["throw"] = TokenKind.Throw, ["true"] = TokenKind.True, ["false"] = TokenKind.False, ["null"] = TokenKind.Null, ["undefined"] = TokenKind.Undefined, ["this"] = TokenKind.This };
    public IReadOnlyList<Token> Scan()
    {
        while (_i < source.Length)
        {
            if (char.IsWhiteSpace(Peek())) { Advance(); continue; }
            if (Peek() == '/' && Peek(1) == '/') { while (_i < source.Length && Peek() != '\n') Advance(); continue; }
            if (Peek() == '/' && Peek(1) == '*') { var l = _line; var c = _column; Advance(); Advance(); while (_i < source.Length && !(Peek() == '*' && Peek(1) == '/')) Advance(); if (_i == source.Length) throw new JsSyntaxException("Unterminated comment", l, c); Advance(); Advance(); continue; }
            var start = _i; var line = _line; var col = _column; var ch = Advance();
            if (char.IsLetter(ch) || ch is '_' or '$') { while (char.IsLetterOrDigit(Peek()) || Peek() is '_' or '$') Advance(); var text = source[start.._i]; Add(Keywords.GetValueOrDefault(text, TokenKind.Identifier), text, start, line, col); continue; }
            if (char.IsDigit(ch)) { while (char.IsDigit(Peek()) || Peek() == '.' && char.IsDigit(Peek(1))) Advance(); Add(TokenKind.Number, source[start.._i], start, line, col); continue; }
            if (ch is '\'' or '"' or '`') { var quote = ch; var b = new System.Text.StringBuilder(); while (_i < source.Length && Peek() != quote) { var x = Advance(); if (x == '\\') { if (_i == source.Length) break; x = Advance(); b.Append(x switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => x }); } else b.Append(x); } if (_i == source.Length) throw new JsSyntaxException("Unterminated string", line, col); Advance(); Add(quote == '`' ? TokenKind.Template : TokenKind.String, b.ToString(), start, line, col); continue; }
            TokenKind kind = ch switch { '(' => TokenKind.LeftParen, ')' => TokenKind.RightParen, '{' => TokenKind.LeftBrace, '}' => TokenKind.RightBrace, '[' => TokenKind.LeftBracket, ']' => TokenKind.RightBracket, ';' => TokenKind.Semicolon, ',' => TokenKind.Comma, '.' => TokenKind.Dot, ':' => TokenKind.Colon, '?' => TokenKind.Question, '+' => TokenKind.Plus, '-' => TokenKind.Minus, '*' => TokenKind.Star, '/' => TokenKind.Slash, '%' => TokenKind.Percent, '!' => Match('=') ? (Match('=') ? TokenKind.StrictNotEqual : TokenKind.NotEqual) : TokenKind.Bang, '=' => Match('=') ? (Match('=') ? TokenKind.StrictEqual : TokenKind.EqualEqual) : TokenKind.Equal, '<' => Match('=') ? TokenKind.LessEqual : TokenKind.Less, '>' => Match('=') ? TokenKind.GreaterEqual : TokenKind.Greater, '&' when Match('&') => TokenKind.AndAnd, '|' when Match('|') => TokenKind.OrOr, _ => throw new JsSyntaxException($"Unexpected character '{ch}'", line, col) };
            Add(kind, source[start.._i], start, line, col);
        }
        Add(TokenKind.End, "", _i, _line, _column); return _tokens;
    }
    private char Peek(int n = 0) => _i + n < source.Length ? source[_i + n] : '\0';
    private char Advance() { var c = source[_i++]; if (c == '\n') { _line++; _column = 1; } else _column++; return c; }
    private bool Match(char c) { if (Peek() != c) return false; Advance(); return true; }
    private void Add(TokenKind kind, string text, int offset, int line, int col) => _tokens.Add(new(kind, text, offset, line, col));
}
