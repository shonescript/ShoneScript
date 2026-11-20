namespace ShoneJS.Parsing;

public enum TokenKind
{
    End, Identifier, Number, String, Template, Let, Const, Var, Function, Return, If, Else,
    While, Throw, True, False, Null, Undefined, This, LeftParen, RightParen, LeftBrace,
    RightBrace, LeftBracket, RightBracket, Semicolon, Comma, Dot, Colon, Question, Plus, Minus,
    Star, Slash, Percent, Bang, Equal, EqualEqual, StrictEqual, NotEqual, StrictNotEqual, Less,
    LessEqual, Greater, GreaterEqual, AndAnd, OrOr,
    TemplateHead, TemplateMiddle, TemplateTail, Typeof, Void, New, Try, Catch, Finally,
    PlusPlus, MinusMinus, Delete, Instanceof, In, Tilde, For, Switch, Case, Default,
    Break, Continue, PlusEqual, MinusEqual, StarEqual, SlashEqual, PercentEqual, BigInt,
    OptionalChain, NullishCoalescing, NullishAssign, BitwiseAnd, BitwiseOr, BitwiseXor,
    BitwiseAndEqual, BitwiseOrEqual, BitwiseXorEqual, ShiftLeft, ShiftRight, ShiftUnsignedRight,
    ShiftLeftEqual, ShiftRightEqual, ShiftUnsignedRightEqual, Power, PowerEqual, Do, AndAndEqual, OrOrEqual, Arrow
}

public readonly record struct SourcePosition(int Offset, int Line, int Column);
public readonly record struct SourceSpan(SourcePosition Start, SourcePosition End);

public readonly record struct Token(TokenKind Kind, string Text, int Offset, int Line, int Column)
{
    public SourcePosition Start => new(Offset, Line, Column);
    public SourcePosition End { get; init; } = new(Offset + Text.Length, Line, Column + Text.Length);
    public SourceSpan Span => new(Start, End);
    public string Raw { get; init; } = Text;
    public bool HasLineTerminatorBefore { get; init; }
    internal bool HasLegacyOctalEscape { get; init; }
}

public sealed class JsSyntaxException(string message, int line, int column) : Exception($"{message} ({line}:{column})")
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public int Offset { get; init; } = -1;
}
