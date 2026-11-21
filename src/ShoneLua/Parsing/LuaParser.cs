// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneLua.Parsing;

internal sealed class LuaParser
{
    private readonly LuaLexer _lexer;
    private readonly string? _fileName;
    private readonly int _maximumDepth;
    private Token _current;
    private Token? _lookahead;
    private SourcePosition _previousEnd;
    private int _depth;

    public LuaParser(string source, string? fileName, int maximumDepth)
    {
        _lexer = new LuaLexer(source, fileName);
        _fileName = fileName;
        _maximumDepth = maximumDepth;
        _current = _lexer.Next();
    }

    public Block Parse()
    {
        var block = ParseBlock(0);
        Expect(Kind.Eof, "end of input");
        return block;
    }

    private Block ParseBlock(int loopDepth)
    {
        CheckDepth();
        try
        {
            var start = _current.Span.Start;
            var statements = new List<Statement>();
            while (!IsBlockEnd(_current.Kind))
            {
                if (Match(Kind.Semicolon)) continue;
                var statement = ParseStatement(loopDepth);
                statements.Add(statement);
                if (statement is ReturnStatement && !IsBlockEnd(_current.Kind))
                    throw Error("'return' must be the final statement in its block.");
            }
            return new Block(statements, new SourceSpan(start, _current.Span.Start));
        }
        finally { _depth--; }
    }

    private Statement ParseStatement(int loopDepth)
    {
        var start = _current.Span.Start;
        switch (_current.Kind)
        {
            case Kind.Local:
                Take();
                if (Match(Kind.Function))
                {
                    var name = Expect(Kind.Name, "a function name").Text;
                    return new LocalFunction(name, ParseFunction(start), Through(start));
                }
                var names = ParseNames();
                var localValues = Match(Kind.Equal) ? ParseExpressions() : [];
                return new Local(names, localValues, Through(start));
            case Kind.Function:
                Take();
                Expression target = FromName(Expect(Kind.Name, "a function name"));
                while (Match(Kind.Dot)) target = Member(target);
                return new Assignment([target], [ParseFunction(start)], Through(start));
            case Kind.If:
                Take();
                var branches = new List<Branch> { ParseBranch(loopDepth) };
                while (Match(Kind.ElseIf)) branches.Add(ParseBranch(loopDepth));
                var otherwise = Match(Kind.Else) ? ParseBlock(loopDepth) : null;
                Expect(Kind.End, "'end'");
                return new IfStatement(branches, otherwise, Through(start));
            case Kind.While:
                Take();
                var condition = ParseExpression();
                Expect(Kind.Do, "'do'");
                var body = ParseBlock(loopDepth + 1);
                Expect(Kind.End, "'end'");
                return new WhileStatement(condition, body, Through(start));
            case Kind.For:
                Take();
                var control = Expect(Kind.Name, "a loop variable").Text;
                Expect(Kind.Equal, "'=' for a numeric for loop");
                var initial = ParseExpression();
                Expect(Kind.Comma, "','");
                var limit = ParseExpression();
                var step = Match(Kind.Comma) ? ParseExpression() : null;
                Expect(Kind.Do, "'do'");
                var loopBody = ParseBlock(loopDepth + 1);
                Expect(Kind.End, "'end'");
                return new NumericFor(control, initial, limit, step, loopBody, Through(start));
            case Kind.Do:
                Take();
                var nested = ParseBlock(loopDepth);
                Expect(Kind.End, "'end'");
                return new DoStatement(nested, Through(start));
            case Kind.Return:
                Take();
                var returned = _current.Kind == Kind.Semicolon || IsBlockEnd(_current.Kind) ? [] : ParseExpressions();
                Match(Kind.Semicolon);
                return new ReturnStatement(returned, Through(start));
            case Kind.Break:
                if (loopDepth == 0) throw Error("'break' must be inside a loop.");
                Take();
                return new BreakStatement(Through(start));
            case Kind.Name or Kind.LeftParen:
                var first = ParseSuffixed();
                if (_current.Kind is not (Kind.Comma or Kind.Equal))
                    return first is Call call ? new CallStatement(call, call.Span) : throw Error("Expected an assignment or function call.");
                var targets = new List<Expression> { RequireTarget(first) };
                while (Match(Kind.Comma)) targets.Add(RequireTarget(ParseSuffixed()));
                Expect(Kind.Equal, "'='");
                return new Assignment(targets, ParseExpressions(), Through(start));
            default:
                throw Error("Expected a statement.");
        }
    }

    private Expression RequireTarget(Expression expression) => expression is Variable or Index ? expression :
        throw new LuaParseException("Invalid assignment target.", expression.Span, _fileName);

    private Branch ParseBranch(int loopDepth)
    {
        var condition = ParseExpression();
        Expect(Kind.Then, "'then'");
        return new Branch(condition, ParseBlock(loopDepth));
    }

    private List<string> ParseNames()
    {
        var names = new List<string> { Expect(Kind.Name, "a name").Text };
        while (Match(Kind.Comma)) names.Add(Expect(Kind.Name, "a name").Text);
        return names;
    }

    private List<Expression> ParseExpressions()
    {
        var expressions = new List<Expression> { ParseExpression() };
        while (Match(Kind.Comma)) expressions.Add(ParseExpression());
        return expressions;
    }

    private Expression ParseExpression(int minimum = 1)
    {
        CheckDepth();
        try
        {
            var start = _current.Span.Start;
            Expression left;
            if (_current.Kind is Kind.Not or Kind.Minus or Kind.Length)
            {
                var op = Take().Kind;
                left = new Unary(op, ParseExpression(7), Through(start));
            }
            else left = ParsePrimary();

            while (Precedence(_current.Kind) is var priority && priority >= minimum)
            {
                var op = Take().Kind;
                var right = ParseExpression(op is Kind.Power or Kind.Concat ? priority : priority + 1);
                left = new Binary(left, op, right, Through(start));
            }
            return left;
        }
        finally { _depth--; }
    }

    private Expression ParsePrimary()
    {
        var token = _current;
        if (token.Kind is Kind.Name or Kind.LeftParen) return ParseSuffixed();
        if (token.Kind == Kind.LeftBrace) return ParseTable();
        if (Match(Kind.Function)) return ParseFunction(token.Span.Start);
        var value = token.Kind switch
        {
            Kind.Number when LuaLexer.TryNumber(token.Text, out var number) => LuaValue.FromNumber(number),
            Kind.Number => throw Error("Malformed number."),
            Kind.String => LuaValue.FromString(token.Text),
            Kind.Nil => LuaValue.Nil,
            Kind.False or Kind.True => LuaValue.FromBoolean(token.Kind == Kind.True),
            _ => throw Error("Expected an expression.")
        };
        Take();
        return new Literal(value, token.Span);
    }

    private Expression ParseSuffixed()
    {
        var start = _current.Span.Start;
        Expression value;
        if (Match(Kind.LeftParen))
        {
            var inner = ParseExpression();
            Expect(Kind.RightParen, "')'");
            value = new Group(inner, Through(start));
        }
        else value = FromName(Expect(Kind.Name, "a name or '('"));
        while (true)
        {
            if (Match(Kind.Dot)) value = Member(value);
            else if (Match(Kind.LeftBracket))
            {
                var key = ParseExpression();
                Expect(Kind.RightBracket, "']'");
                value = new Index(value, key, Through(start));
            }
            else if (Match(Kind.LeftParen))
            {
                var arguments = _current.Kind == Kind.RightParen ? [] : ParseExpressions();
                Expect(Kind.RightParen, "')'");
                value = new Call(value, arguments, Through(start));
            }
            else if (_current.Kind == Kind.LeftBrace) value = new Call(value, [ParseTable()], Through(start));
            else if (_current.Kind == Kind.String)
            {
                var token = Take();
                value = new Call(value, [new Literal(LuaValue.FromString(token.Text), token.Span)], Through(start));
            }
            else return value;
        }
    }

    private Index Member(Expression target)
    {
        var member = Expect(Kind.Name, "a field name");
        return new Index(target, new Literal(LuaValue.FromString(member.Text), member.Span), Through(target.Span.Start));
    }

    private FunctionExpression ParseFunction(SourcePosition start)
    {
        Expect(Kind.LeftParen, "'('");
        var parameters = _current.Kind == Kind.RightParen ? [] : ParseNames();
        Expect(Kind.RightParen, "')'");
        var body = ParseBlock(0);
        Expect(Kind.End, "'end'");
        return new FunctionExpression(parameters, body, Through(start));
    }

    private TableExpression ParseTable()
    {
        var start = Expect(Kind.LeftBrace, "'{'").Span.Start;
        var fields = new List<Field>();
        while (_current.Kind != Kind.RightBrace)
        {
            var fieldStart = _current.Span.Start;
            Expression? key = null;
            if (Match(Kind.LeftBracket))
            {
                key = ParseExpression();
                Expect(Kind.RightBracket, "']'");
                Expect(Kind.Equal, "'='");
            }
            else if (_current.Kind == Kind.Name && Peek().Kind == Kind.Equal)
            {
                var name = Take();
                Take();
                key = new Literal(LuaValue.FromString(name.Text), name.Span);
            }
            fields.Add(new Field(key, ParseExpression(), Through(fieldStart)));
            if (!Match(Kind.Comma) && !Match(Kind.Semicolon)) break;
        }
        Expect(Kind.RightBrace, "'}'");
        return new TableExpression(fields, Through(start));
    }

    private void CheckDepth()
    {
        if (_depth >= _maximumDepth) throw Error("Maximum Lua parse depth exceeded.");
        _depth++;
    }

    private static bool IsBlockEnd(Kind kind) => kind is Kind.Eof or Kind.End or Kind.Else or Kind.ElseIf;
    private static Variable FromName(Token token) => new(token.Text, token.Span);
    private static int Precedence(Kind kind) => kind switch
    {
        Kind.Or => 1, Kind.And => 2,
        Kind.EqualEqual or Kind.NotEqual or Kind.Less or Kind.LessEqual or Kind.Greater or Kind.GreaterEqual => 3,
        Kind.Concat => 4, Kind.Plus or Kind.Minus => 5,
        Kind.Star or Kind.Slash or Kind.FloorDiv or Kind.Percent => 6, Kind.Power => 8,
        _ => 0
    };

    private Token Peek() => _lookahead ??= _lexer.Next();
    private Token Take()
    {
        var token = _current;
        _previousEnd = token.Span.End;
        _current = _lookahead ?? _lexer.Next();
        _lookahead = null;
        return token;
    }

    private bool Match(Kind kind)
    {
        if (_current.Kind != kind) return false;
        Take();
        return true;
    }

    private Token Expect(Kind kind, string description) => _current.Kind == kind ? Take() : throw Error($"Expected {description}.");
    private SourceSpan Through(SourcePosition start) => new(start, _previousEnd);
    private LuaParseException Error(string message) => new(message, _current.Span, _fileName);
}
