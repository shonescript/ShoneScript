// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneLua.Parsing;

internal enum Kind
{
    Eof, Name, Number, String,
    And, Break, Do, Else, ElseIf, End, False, For, Function, If, In, Local, Nil, Not, Or, Repeat, Return, Then, True, Until, While, Goto,
    Plus, Minus, Star, Slash, FloorDiv, Percent, Power, Length, Concat,
    Equal, EqualEqual, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    LeftParen, RightParen, LeftBracket, RightBracket, LeftBrace, RightBrace, Comma, Semicolon, Dot
}

internal readonly record struct Token(Kind Kind, string Text, SourceSpan Span);
internal sealed record Block(IReadOnlyList<Statement> Statements, SourceSpan Span);

internal abstract record Statement(SourceSpan Span);
internal sealed record Assignment(IReadOnlyList<Expression> Targets, IReadOnlyList<Expression> Values, SourceSpan Span) : Statement(Span);
internal sealed record Local(IReadOnlyList<string> Names, IReadOnlyList<Expression> Values, SourceSpan Span) : Statement(Span);
internal sealed record LocalFunction(string Name, FunctionExpression Function, SourceSpan Span) : Statement(Span);
internal sealed record CallStatement(Call Call, SourceSpan Span) : Statement(Span);
internal sealed record IfStatement(IReadOnlyList<Branch> Branches, Block? Else, SourceSpan Span) : Statement(Span);
internal sealed record Branch(Expression Condition, Block Body);
internal sealed record WhileStatement(Expression Condition, Block Body, SourceSpan Span) : Statement(Span);
internal sealed record NumericFor(string Name, Expression Start, Expression Limit, Expression? Step, Block Body, SourceSpan Span) : Statement(Span);
internal sealed record DoStatement(Block Body, SourceSpan Span) : Statement(Span);
internal sealed record ReturnStatement(IReadOnlyList<Expression> Values, SourceSpan Span) : Statement(Span);
internal sealed record BreakStatement(SourceSpan Span) : Statement(Span);

internal abstract record Expression(SourceSpan Span);
internal sealed record Literal(LuaValue Value, SourceSpan Span) : Expression(Span);
internal sealed record Variable(string Name, SourceSpan Span) : Expression(Span);
internal sealed record Group(Expression Inner, SourceSpan Span) : Expression(Span);
internal sealed record Unary(Kind Operator, Expression Operand, SourceSpan Span) : Expression(Span);
internal sealed record Binary(Expression Left, Kind Operator, Expression Right, SourceSpan Span) : Expression(Span);
internal sealed record Index(Expression Target, Expression Key, SourceSpan Span) : Expression(Span);
internal sealed record Call(Expression Target, IReadOnlyList<Expression> Arguments, SourceSpan Span) : Expression(Span);
internal sealed record FunctionExpression(IReadOnlyList<string> Parameters, Block Body, SourceSpan Span) : Expression(Span);
internal sealed record TableExpression(IReadOnlyList<Field> Fields, SourceSpan Span) : Expression(Span);
internal sealed record Field(Expression? Key, Expression Value, SourceSpan Span);
