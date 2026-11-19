namespace ShoneJS.Parsing;

public enum TokenKind { End, Identifier, Number, String, Template, Let, Const, Var, Function, Return, If, Else, While, Throw, True, False, Null, Undefined, This, LeftParen, RightParen, LeftBrace, RightBrace, LeftBracket, RightBracket, Semicolon, Comma, Dot, Colon, Question, Plus, Minus, Star, Slash, Percent, Bang, Equal, EqualEqual, StrictEqual, NotEqual, StrictNotEqual, Less, LessEqual, Greater, GreaterEqual, AndAnd, OrOr }
public readonly record struct Token(TokenKind Kind, string Text, int Offset, int Line, int Column);
public sealed class JsSyntaxException(string message, int line, int column) : Exception($"{message} ({line}:{column})") { public int Line { get; } = line; public int Column { get; } = column; }
