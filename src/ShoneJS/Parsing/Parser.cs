// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using Sdcb.Arithmetic;

namespace ShoneScript.Parsing;

public enum ParseGoal { Script, Module }

public sealed partial class Parser(IReadOnlyList<Token> tokens, ParseGoal goal = ParseGoal.Script, bool forceStrict = false, bool typeScript = false)
{
    private int _index;
    private int _functionDepth, _loopDepth, _switchDepth;
    private int _pendingTypeClosers;
    private List<TypeNode?> _lastParameterTypes = [];
    private bool _strict = forceStrict || goal == ParseGoal.Module;

    public bool IsStrict => _strict;
    public ParseGoal Goal => goal;

    private Token Current => tokens[Math.Min(_index, tokens.Count - 1)];
    private Token Previous => tokens[_index - 1];
    private bool LineBreak => Current.HasLineTerminatorBefore || _index > 0 && Previous.End.Line < Current.Line;
    private Token Take() => tokens[_index++];

    private bool Match(TokenKind kind)
    {
        if (Current.Kind != kind) return false;
        Take();
        return true;
    }

    private Token Need(TokenKind kind) => Current.Kind == kind ? Take() : throw Error($"Expected {kind}", Current);

    // Parse a complete JavaScript script/module. Module goal is strict by definition.
    public List<Stmt> Parse() => StatementList(TokenKind.End, directives: true);

    // TypeScript declarations are accepted only through this explicit entrypoint/mode.
    public List<Stmt> ParseTypeScript()
    {
        if (!typeScript) throw new InvalidOperationException("Parser was not created in TypeScript mode");
        var statements = StatementList(TokenKind.End, directives: true);
        if (_pendingTypeClosers != 0) throw Error("Unmatched generic type closer", Current);
        return statements;
    }

    // Parse exactly one expression; unlike statement parsing, trailing tokens are an error.
    public Expr ParseExpression()
    {
        var expression = Expression();
        if (Current.Kind != TokenKind.End) throw Error("Unexpected token after expression", Current);
        return expression;
    }

    // Directive prologues must be handled before ordinary statements so "use strict"
    // can affect validation of the rest of the containing scope.
    private List<Stmt> StatementList(TokenKind end, bool directives = false)
    {
        var statements = new List<Stmt>();
        var functionsAreLexical = end == TokenKind.End ? goal == ParseGoal.Module : !directives;
        List<Token> directiveTokens = [];
        while (Current.Kind != end && Current.Kind != TokenKind.End)
        {
            var start = Current;
            var statement = typeScript ? TypeScriptStatement() ?? Statement() : Statement();
            statements.Add(statement);
            if (!directives) continue;
            if (start.Kind != TokenKind.String || statement is not ExprStmt { Expression: LiteralExpr literal } ||
                literal.Span.End != start.End)
            {
                directives = false;
                continue;
            }
            directiveTokens.Add(start);
            if (start.Raw is "'use strict'" or "\"use strict\"")
            {
                _strict = true;
                foreach (var token in directiveTokens) ValidateString(token);
                directiveTokens.Clear();
            }
        }
        ValidateDeclarations(statements, functionsAreLexical);
        return statements;
    }

    // Implements automatic semicolon insertion at EOF, before '}', or after a line break.
    private void Semicolon()
    {
        if (Match(TokenKind.Semicolon) || Current.Kind is TokenKind.End or TokenKind.RightBrace || LineBreak) return;
        throw Error("Expected semicolon", Current);
    }

    private Stmt Statement(bool allowDeclaration = true)
    {
        var start = Current;
        Stmt statement;
        switch (start.Kind)
        {
            case TokenKind.Semicolon:
                Take();
                statement = new ExprStmt(new LiteralExpr(null) { Span = start.Span });
                break;
            case TokenKind.LeftBrace:
                return Block();
            case TokenKind.Let:
            case TokenKind.Const:
            case TokenKind.Var:
                if (!allowDeclaration && start.Kind != TokenKind.Var)
                    throw Error("Unexpected lexical declaration", start);
                statement = VariableDeclaration();
                Semicolon();
                break;
            case TokenKind.Function:
                if (!allowDeclaration && _strict) throw Error("Unexpected function declaration", start);
                Take();
                var name = Need(TokenKind.Identifier);
                var typeParameters = typeScript && Current.Kind == TokenKind.Less ? ParseTypeParameters() : [];
                var parameters = Parameters();
                var parameterTypes = _lastParameterTypes.ToArray();
                var returnType = typeScript && Match(TokenKind.Colon) ? ParseType() : null;
                statement = new FunctionStmt(name.Text, parameters.Select(token => token.Text).ToList(), FunctionBody(name, parameters))
                {
                    ParameterTypes = parameterTypes,
                    TypeParameters = typeParameters,
                    ReturnType = returnType
                };
                break;
            case TokenKind.Return:
                if (_functionDepth == 0) throw Error("Return outside a function", start);
                Take();
                var value = LineBreak || Current.Kind is TokenKind.Semicolon or TokenKind.RightBrace or TokenKind.End
                    ? null : Expression();
                Semicolon();
                statement = new ReturnStmt(value);
                break;
            case TokenKind.Throw:
                Take();
                if (LineBreak) throw Error("Line terminator after throw", Current);
                statement = new ThrowStmt(Expression());
                Semicolon();
                break;
            case TokenKind.If:
                Take();
                var test = ParenthesizedExpression();
                var then = Statement(allowDeclaration: false);
                statement = new IfStmt(test, then, Match(TokenKind.Else) ? Statement(allowDeclaration: false) : null);
                break;
            case TokenKind.While:
                Take();
                var condition = ParenthesizedExpression();
                statement = new WhileStmt(condition, LoopBody());
                break;
            case TokenKind.Do:
                Take();
                var loopBody = LoopBody();
                Need(TokenKind.While);
                statement = new DoWhileStmt(ParenthesizedExpression(), loopBody);
                Match(TokenKind.Semicolon);
                break;
            case TokenKind.For:
                Take();
                statement = ForStatement();
                break;
            case TokenKind.Switch:
                Take();
                statement = SwitchStatement();
                break;
            case TokenKind.Break:
                if (_loopDepth == 0 && _switchDepth == 0) throw Error("Break outside a loop or switch", start);
                Take();
                Semicolon();
                statement = new BreakStmt();
                break;
            case TokenKind.Continue:
                if (_loopDepth == 0) throw Error("Continue outside a loop", start);
                Take();
                Semicolon();
                statement = new ContinueStmt();
                break;
            case TokenKind.Try:
                Take();
                statement = TryStatement();
                break;
            default:
                statement = new ExprStmt(Expression());
                Semicolon();
                break;
        }
        return statement with { Span = SpanFrom(start.Start) };
    }

    private BlockStmt Block(bool directives = false)
    {
        var start = Need(TokenKind.LeftBrace);
        var body = StatementList(TokenKind.RightBrace, directives);
        Need(TokenKind.RightBrace);
        return new BlockStmt(body) { Span = SpanFrom(start.Start), IsStrict = _strict };
    }

    private Stmt VariableDeclaration()
    {
        var start = Take();
        var declarations = new List<VarStmt>();
        HashSet<string> names = new(StringComparer.Ordinal);
        do
        {
            var name = Need(TokenKind.Identifier);
            ValidateBinding(name);
            if (start.Kind != TokenKind.Var && !names.Add(name.Text))
                throw Error("Duplicate lexical declaration", name);
            var annotation = typeScript && Match(TokenKind.Colon) ? ParseType() : null;
            var init = Match(TokenKind.Equal) ? Expression(1) : null;
            if (start.Kind == TokenKind.Const && init is null) throw Error("const declaration requires an initializer", Current);
            declarations.Add(new VarStmt(start.Kind, name.Text, init)
            {
                TypeAnnotation = annotation,
                Span = SpanFrom(declarations.Count == 0 ? start.Start : name.Start)
            });
        } while (Match(TokenKind.Comma));
        return declarations.Count == 1 ? declarations[0] : new VarListStmt(declarations) { Span = SpanFrom(start.Start) };
    }

    private List<Token> Parameters()
    {
        Need(TokenKind.LeftParen);
        var parameters = new List<Token>();
        _lastParameterTypes = [];
        while (Current.Kind != TokenKind.RightParen)
        {
            parameters.Add(Need(TokenKind.Identifier));
            Match(TokenKind.Question);
            _lastParameterTypes.Add(typeScript && Match(TokenKind.Colon) ? ParseType() : null);
            if (!Match(TokenKind.Comma)) break;
        }
        Need(TokenKind.RightParen);
        return parameters;
    }

    private BlockStmt FunctionBody(Token? name, List<Token> parameters)
    {
        var outerStrict = _strict;
        var outerLoopDepth = _loopDepth;
        var outerSwitchDepth = _switchDepth;
        _loopDepth = _switchDepth = 0;
        _functionDepth++;
        try
        {
            var body = Block(directives: true);
            if (name.HasValue) ValidateBinding(name.Value);
            var lexicalNames = DirectDeclarations(body.Body)
                .Where(declaration => declaration.Kind != TokenKind.Var)
                .Select(declaration => declaration.Name)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                ValidateBinding(parameter);
                if (lexicalNames.Contains(parameter.Text)) throw Error("Parameter conflicts with a lexical declaration", parameter);
                if (_strict && !names.Add(parameter.Text)) throw Error("Duplicate parameter in strict mode", parameter);
            }
            return body;
        }
        finally
        {
            _strict = outerStrict;
            _loopDepth = outerLoopDepth;
            _switchDepth = outerSwitchDepth;
            _functionDepth--;
        }
    }

    private Stmt LoopBody()
    {
        _loopDepth++;
        try { return Statement(allowDeclaration: false); }
        finally { _loopDepth--; }
    }

    private ForStmt ForStatement()
    {
        Need(TokenKind.LeftParen);
        Stmt? init = Current.Kind switch
        {
            TokenKind.Semicolon => null,
            TokenKind.Var or TokenKind.Let or TokenKind.Const => VariableDeclaration(),
            _ => ExpressionStatementWithoutSemicolon()
        };
        Need(TokenKind.Semicolon);
        var test = Current.Kind == TokenKind.Semicolon ? null : Expression();
        Need(TokenKind.Semicolon);
        var update = Current.Kind == TokenKind.RightParen ? null : Expression();
        Need(TokenKind.RightParen);
        var body = LoopBody();
        if (init is not null)
        {
            var lexical = DirectDeclarations([init]).Where(declaration => declaration.Kind != TokenKind.Var)
                .Select(declaration => declaration.Name).ToHashSet(StringComparer.Ordinal);
            if (lexical.Count != 0)
                foreach (var (name, start) in VarDeclaredNames([body], includeFunctions: false))
                    if (lexical.Contains(name)) throw Error("Declaration conflicts with a loop binding", start);
        }
        return new ForStmt(init, test, update, body);
    }

    private ExprStmt ExpressionStatementWithoutSemicolon()
    {
        var expression = Expression();
        return new ExprStmt(expression) { Span = expression.Span };
    }

    private SwitchStmt SwitchStatement()
    {
        var discriminant = ParenthesizedExpression();
        Need(TokenKind.LeftBrace);
        List<SwitchCase> cases = [];
        var sawDefault = false;
        _switchDepth++;
        try
        {
            while (Current.Kind != TokenKind.RightBrace)
            {
                var start = Current;
                Expr? test;
                if (Match(TokenKind.Case)) test = Expression();
                else
                {
                    Need(TokenKind.Default);
                    if (sawDefault) throw Error("Duplicate default clause", start);
                    sawDefault = true;
                    test = null;
                }
                Need(TokenKind.Colon);
                List<Stmt> body = [];
                while (Current.Kind is not (TokenKind.Case or TokenKind.Default or TokenKind.RightBrace or TokenKind.End))
                    body.Add(Statement());
                cases.Add(new SwitchCase(test, body) { Span = SpanFrom(start.Start) });
            }
            Take();
            ValidateDeclarations(cases.SelectMany(clause => clause.Body), functionsAreLexical: true);
        }
        finally { _switchDepth--; }
        return new SwitchStmt(discriminant, cases);
    }

    private TryStmt TryStatement()
    {
        var body = Block();
        string? catchName = null;
        BlockStmt? catchBody = null;
        if (Match(TokenKind.Catch))
        {
            if (Match(TokenKind.LeftParen))
            {
                var binding = Need(TokenKind.Identifier);
                ValidateBinding(binding);
                catchName = binding.Text;
                Need(TokenKind.RightParen);
            }
            catchBody = Block();
            if (catchName is not null)
                foreach (var (name, start) in LexicalNames(catchBody.Body, functionsAreLexical: true))
                    if (name == catchName) throw Error("Declaration conflicts with a catch binding", start);
        }
        var finallyBody = Match(TokenKind.Finally) ? Block() : null;
        if (catchBody is null && finallyBody is null) throw Error("Expected catch or finally", Current);
        return new TryStmt(body, catchName, catchBody, finallyBody);
    }

    private Expr Expression(int minPrecedence = 0)
    {
        if (typeScript && minPrecedence <= 1 && Current.Kind is (TokenKind.LeftParen or TokenKind.Less) && TryParenthesizedArrow(out var arrow))
            return ContinueAfterPostfix(arrow, minPrecedence);
        var left = Primary();
        if (typeScript && minPrecedence <= 1 && left is IdentifierExpr { IsParenthesized: false } identifier && !LineBreak && Match(TokenKind.Arrow))
            return ContinueAfterPostfix(ParseArrowBody([new ArrowParameter(identifier.Name)], null, left.Span.Start), minPrecedence);
        while (true)
        {
            if (typeScript && minPrecedence <= 14 && Current.Kind == TokenKind.Less && TryGenericCall(left, out var genericCall))
            {
                left = genericCall;
                continue;
            }
            if (minPrecedence <= 14 && Current.Kind == TokenKind.OptionalChain)
            {
                left = OptionalChain(left);
                continue;
            }
            if (minPrecedence <= 14 && TryMember(left, out var member))
            {
                left = member;
                continue;
            }
            if (minPrecedence <= 14 && Current.Kind == TokenKind.LeftParen)
            {
                left = new CallExpr(left, Arguments()) { Span = SpanFrom(left.Span.Start) };
                continue;
            }
            if (typeScript && minPrecedence <= 14 && !LineBreak && Current.Kind == TokenKind.Bang)
            {
                Take();
                left = new NonNullExpr(left) { Span = SpanFrom(left.Span.Start) };
                continue;
            }
            if (minPrecedence <= 14 && !LineBreak && Current.Kind is TokenKind.PlusPlus or TokenKind.MinusMinus)
            {
                var op = Take();
                ValidateTarget(left, op);
                return ContinueAfterPostfix(new UpdateExpr(left, op.Kind, Prefix: false) { Span = SpanFrom(left.Span.Start) }, minPrecedence);
            }
            return ContinueAfterPostfix(left, minPrecedence);
        }
    }

    // TypeScript 'as'/'satisfies' assertions share the postfix stage with conditional,
    // assignment, and binary operators, but are never emitted as JavaScript operations.
    private Expr ContinueAfterPostfix(Expr left, int minPrecedence)
    {
        while (true)
        {
            if (typeScript && minPrecedence <= 9 && !LineBreak && Current.Kind == TokenKind.Identifier && Current.Text is "as" or "satisfies")
            {
                var keyword = Take();
                var type = ParseType();
                left = keyword.Text == "as"
                    ? new TypeAssertionExpr(left, type) { Span = SpanFrom(left.Span.Start) }
                    : new SatisfiesExpr(left, type) { Span = SpanFrom(left.Span.Start) };
                continue;
            }
            if (minPrecedence <= 2 && Match(TokenKind.Question))
            {
                var then = Expression(1);
                Need(TokenKind.Colon);
                left = new ConditionalExpr(left, then, Expression(1)) { Span = SpanFrom(left.Span.Start) };
                continue;
            }
            var precedence = Precedence(Current.Kind);
            if (precedence < minPrecedence) return left;
            var op = Take();
            var start = left.Span.Start;
            if (precedence == 1)
            {
                ValidateTarget(left, op);
                var right = Expression(1);
                left = op.Kind == TokenKind.Equal ? new AssignExpr(left, right) : new CompoundAssignExpr(left, UnderlyingOperator(op.Kind), right);
            }
            else if (op.Kind == TokenKind.Comma)
            {
                var items = new List<Expr> { left, Expression(1) };
                while (Match(TokenKind.Comma)) items.Add(Expression(1));
                left = new SequenceExpr(items);
            }
            else
            {
                if (op.Kind == TokenKind.Power && left is UnaryExpr { IsParenthesized: false })
                    throw Error("An unparenthesized unary expression cannot precede '**'", op);
                var right = Expression(op.Kind == TokenKind.Power ? precedence : precedence + 1);
                if (MixesNullishAndLogical(op.Kind, left) || MixesNullishAndLogical(op.Kind, right))
                    throw Error("Nullish coalescing cannot mix with logical operators without parentheses", op);
                left = new BinaryExpr(left, op.Kind, right);
            }
            left = left with { Span = SpanFrom(start) };
        }
    }

    private Expr Primary()
    {
        var token = Take();
        Expr expression = token.Kind switch
        {
            TokenKind.Number => new LiteralExpr(Number(token)),
            TokenKind.BigInt => new BigIntExpr(Integer(token.Text[..^1])),
            TokenKind.String => StringLiteral(token),
            TokenKind.Template or TokenKind.TemplateHead => TemplateLiteral(token),
            TokenKind.True => new LiteralExpr(true),
            TokenKind.False => new LiteralExpr(false),
            TokenKind.Null => new LiteralExpr(null),
            TokenKind.Identifier or TokenKind.Undefined => Identifier(token),
            TokenKind.This => new ThisExpr(),
            TokenKind.Bang or TokenKind.Minus or TokenKind.Plus or TokenKind.Typeof or TokenKind.Void or TokenKind.Delete or TokenKind.Tilde => Unary(token),
            TokenKind.PlusPlus or TokenKind.MinusMinus => PrefixUpdate(token),
            TokenKind.LeftParen => Group(),
            TokenKind.Less when typeScript => ParseAngleAssertion(),
            TokenKind.LeftBracket => ArrayLiteral(),
            TokenKind.LeftBrace => ObjectLiteral(),
            TokenKind.Function => FunctionExpression(),
            TokenKind.New => NewExpression(),
            _ => throw Error("Expected expression", token)
        };
        return expression with { Span = SpanFrom(token.Start) };
    }

    private Expr Unary(Token token)
    {
        var operand = Expression(14);
        if (_strict && token.Kind == TokenKind.Delete && operand is IdentifierExpr)
            throw Error("Cannot delete an unqualified identifier in strict mode", token);
        return new UnaryExpr(token.Kind, operand);
    }

    private Expr PrefixUpdate(Token token)
    {
        var operand = Expression(14);
        ValidateTarget(operand, token);
        return new UpdateExpr(operand, token.Kind, Prefix: true);
    }

    private LiteralExpr StringLiteral(Token token)
    {
        ValidateString(token);
        return new LiteralExpr(token.Text);
    }

    private TemplateExpr TemplateLiteral(Token first)
    {
        var quasis = new List<TemplateElement> { new(first.Raw, first.Text, first.Span) };
        List<Expr> expressions = [];
        if (first.Kind == TokenKind.TemplateHead)
        {
            do
            {
                expressions.Add(Expression());
                if (Current.Kind is not (TokenKind.TemplateMiddle or TokenKind.TemplateTail))
                    throw Error("Expected end of template substitution", Current);
                var part = Take();
                quasis.Add(new TemplateElement(part.Raw, part.Text, part.Span));
            } while (Previous.Kind == TokenKind.TemplateMiddle);
        }
        return new TemplateExpr(quasis, expressions);
    }

    private IdentifierExpr Identifier(Token token)
    {
        if (IsReserved(token.Text)) throw Error("Unexpected reserved identifier", token);
        return new IdentifierExpr(token.Text);
    }

    private Expr ParenthesizedExpression()
    {
        Need(TokenKind.LeftParen);
        return Group();
    }

    private Expr Group()
    {
        var expression = Expression();
        Need(TokenKind.RightParen);
        return expression with { IsParenthesized = true };
    }

    private ArrayExpr ArrayLiteral()
    {
        var elements = new List<Expr>();
        while (Current.Kind != TokenKind.RightBracket)
        {
            if (Match(TokenKind.Comma))
            {
                elements.Add(new ArrayHoleExpr { Span = Previous.Span });
                continue;
            }
            elements.Add(Expression(1));
            if (!Match(TokenKind.Comma)) break;
        }
        Need(TokenKind.RightBracket);
        return new ArrayExpr(elements);
    }

    private List<Expr> Arguments()
    {
        Need(TokenKind.LeftParen);
        return DelimitedExpressions(TokenKind.RightParen);
    }

    private List<Expr> DelimitedExpressions(TokenKind end)
    {
        List<Expr> expressions = [];
        while (Current.Kind != end)
        {
            expressions.Add(Expression(1));
            if (!Match(TokenKind.Comma)) break;
        }
        Need(end);
        return expressions;
    }

    private ObjectExpr ObjectLiteral()
    {
        var properties = new List<(string Key, Expr Value)>();
        var hasProto = false;
        while (Current.Kind != TokenKind.RightBrace)
        {
            var key = Take();
            if (!IsIdentifierName(key) && key.Kind is not (TokenKind.Number or TokenKind.String))
                throw Error("Expected property name", key);
            if (key.Kind == TokenKind.String) ValidateString(key);
            var name = key.Kind == TokenKind.Number ? Number(key).ToString("G", CultureInfo.InvariantCulture) : key.Text;
            Expr value;
            if (Match(TokenKind.Colon))
            {
                if (name == "__proto__")
                {
                    if (hasProto) throw Error("Duplicate __proto__ property", key);
                    hasProto = true;
                }
                value = Expression(1);
            }
            else if (Current.Kind == TokenKind.LeftParen)
            {
                var typeParameters = typeScript && Current.Kind == TokenKind.Less ? ParseTypeParameters() : [];
                var parameters = Parameters();
                var parameterTypes = _lastParameterTypes.ToArray();
                var returnType = typeScript && Match(TokenKind.Colon) ? ParseType() : null;
                var body = FunctionBody(null, parameters);
                if (parameters.Select(parameter => parameter.Text).Distinct(StringComparer.Ordinal).Count() != parameters.Count)
                    throw Error("Duplicate method parameters", key);
                value = new FunctionExpr(parameters.Select(parameter => parameter.Text).ToList(), body)
                {
                    IsMethod = true,
                    ParameterTypes = parameterTypes,
                    TypeParameters = typeParameters,
                    ReturnType = returnType,
                    Span = SpanFrom(key.Span.Start)
                };
            }
            else if (key.Kind == TokenKind.Identifier) value = Identifier(key) with { Span = key.Span };
            else throw Error("Expected colon after property name", Current);
            properties.Add((name, value));
            if (!Match(TokenKind.Comma)) break;
        }
        Need(TokenKind.RightBrace);
        return new ObjectExpr(properties);
    }

    private FunctionExpr FunctionExpression()
    {
        Token? name = Current.Kind == TokenKind.Identifier ? Take() : null;
        var typeParameters = typeScript && Current.Kind == TokenKind.Less ? ParseTypeParameters() : [];
        var parameters = Parameters();
        var parameterTypes = _lastParameterTypes.ToArray();
        var returnType = typeScript && Match(TokenKind.Colon) ? ParseType() : null;
        return new FunctionExpr(parameters.Select(token => token.Text).ToList(), FunctionBody(name, parameters))
        {
            Name = name?.Text,
            ParameterTypes = parameterTypes,
            TypeParameters = typeParameters,
            ReturnType = returnType
        };
    }

    private NewExpr NewExpression()
    {
        var target = Primary();
        while (TryMember(target, out var member)) target = member;
        if (Current.Kind == TokenKind.OptionalChain) throw Error("Optional chain cannot be a constructor target", Current);
        return new NewExpr(target, Current.Kind == TokenKind.LeftParen ? Arguments() : []);
    }

    private OptionalChainExpr OptionalChain(Expr target)
    {
        List<OptionalChainSegment> segments = [];
        while (true)
        {
            var optional = Match(TokenKind.OptionalChain);
            if (Current.Kind == TokenKind.LeftParen) segments.Add(new ChainCall(Arguments(), optional));
            else if (Match(TokenKind.LeftBracket))
            {
                var key = Expression();
                Need(TokenKind.RightBracket);
                segments.Add(new ChainMember(key, optional));
            }
            else if (optional || Match(TokenKind.Dot)) segments.Add(new ChainMember(PropertyName(), optional));
            else break;
        }
        return new OptionalChainExpr(target, segments) { Span = SpanFrom(target.Span.Start) };
    }

    private LiteralExpr PropertyName()
    {
        var property = Take();
        if (!IsIdentifierName(property)) throw Error("Expected property name", property);
        return new LiteralExpr(property.Text) { Span = property.Span };
    }

    private bool TryMember(Expr target, out Expr member)
    {
        Expr key;
        if (Match(TokenKind.Dot)) key = PropertyName();
        else if (Match(TokenKind.LeftBracket))
        {
            key = Expression();
            Need(TokenKind.RightBracket);
        }
        else
        {
            member = target;
            return false;
        }
        member = new MemberExpr(target, key) { Span = SpanFrom(target.Span.Start) };
        return true;
    }

    // Conversion is context-sensitive: legacy leading-zero literals are valid in sloppy
    // JavaScript but rejected in strict scripts and modules.
    private double Number(Token token)
    {
        var text = token.Text;
        if (text.Length > 1 && text[0] == '0' && char.IsAsciiDigit(text[1]))
        {
            if (_strict) throw Error("Leading-zero numeric literal in strict mode", token);
            if (text.All(ch => ch is >= '0' and <= '7')) return (double)Integer(text, legacyOctal: true);
        }
        if (text.Length > 2 && text[0] == '0' && text[1] is 'x' or 'X' or 'o' or 'O' or 'b' or 'B')
            return (double)Integer(text);
        if (double.TryParse(text.Replace("_", "", StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        throw Error("Invalid numeric literal", token);
    }

    private static BigInt Integer(string text, bool legacyOctal = false)
    {
        var radix = legacyOctal ? 8 : 10;
        var start = 0;
        if (text.Length > 2 && text[0] == '0')
        {
            radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'o' => 8, 'b' => 2, _ => radix };
            if (!legacyOctal && radix != 10) start = 2;
        }
        BigInt result = 0;
        foreach (var digit in text.AsSpan(start))
        {
            if (digit == '_') continue;
            var value = char.IsAsciiDigit(digit) ? digit - '0' : char.ToLowerInvariant(digit) - 'a' + 10;
            result = result * radix + value;
        }
        return result;
    }

    private bool IsReserved(string identifier) => identifier is "enum" or "class" or "extends" or "super" or
        "import" or "export" or "do" or "with" or "debugger" || goal == ParseGoal.Module && identifier == "await" ||
        _strict && identifier is "implements" or "interface" or "package" or "private" or "protected" or "public" or "static" or "yield" &&
        !(typeScript && identifier is "implements" or "interface" or "private" or "protected" or "public" or "static");

    private void ValidateBinding(Token token)
    {
        if (IsReserved(token.Text) || _strict && token.Text is "eval" or "arguments")
            throw Error("Invalid binding identifier", token);
    }

    private static IEnumerable<VarStmt> DirectDeclarations(IEnumerable<Stmt> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is VarStmt declaration) yield return declaration;
            else if (statement is VarListStmt list)
                foreach (var item in list.Declarations) yield return item;
        }
    }

    private static IEnumerable<Stmt> NestedStatements(Stmt statement) => statement switch
    {
        BlockStmt block => block.Body,
        IfStmt { Else: not null } conditional => [conditional.Then, conditional.Else],
        IfStmt conditional => [conditional.Then],
        WhileStmt loop => [loop.Body],
        DoWhileStmt loop => [loop.Body],
        ForStmt { Init: not null } loop => [loop.Init, loop.Body],
        ForStmt loop => [loop.Body],
        SwitchStmt selection => selection.Cases.SelectMany(clause => clause.Body),
        TryStmt handling => new[] { handling.Body, handling.CatchBody, handling.FinallyBody }.OfType<Stmt>(),
        _ => []
    };

    private static IEnumerable<(string Name, SourcePosition Start)> VarDeclaredNames(IEnumerable<Stmt> statements, bool includeFunctions)
    {
        foreach (var declaration in DirectDeclarations(statements))
            if (declaration.Kind == TokenKind.Var) yield return (declaration.Name, declaration.Span.Start);
        foreach (var statement in statements)
        {
            if (includeFunctions && statement is FunctionStmt function)
                yield return (function.Name, function.Span.Start);
            foreach (var name in VarDeclaredNames(NestedStatements(statement), includeFunctions: false))
                yield return name;
        }
    }

    private static IEnumerable<(string Name, SourcePosition Start)> LexicalNames(IEnumerable<Stmt> statements, bool functionsAreLexical)
    {
        foreach (var declaration in DirectDeclarations(statements))
            if (declaration.Kind != TokenKind.Var) yield return (declaration.Name, declaration.Span.Start);
        if (functionsAreLexical)
            foreach (var statement in statements)
                if (statement is FunctionStmt function) yield return (function.Name, function.Span.Start);
    }

    private void ValidateDeclarations(IEnumerable<Stmt> statements, bool functionsAreLexical)
    {
        HashSet<string> lexical = new(StringComparer.Ordinal);
        if (typeScript)
            foreach (var enumeration in statements.OfType<EnumStmt>())
                if (!lexical.Add(enumeration.Name)) throw Error("Duplicate enum declaration", enumeration.Span.Start);
        foreach (var (name, start) in LexicalNames(statements, functionsAreLexical: false))
            if (!lexical.Add(name)) throw Error("Duplicate lexical declaration", start);
        if (functionsAreLexical)
        {
            HashSet<string> functions = new(StringComparer.Ordinal);
            foreach (var function in statements.OfType<FunctionStmt>())
                if (lexical.Contains(function.Name) || !functions.Add(function.Name) && _strict)
                    throw Error("Duplicate lexical declaration", function.Span.Start);
            lexical.UnionWith(functions);
        }
        if (lexical.Count == 0) return;
        foreach (var (name, start) in VarDeclaredNames(statements, includeFunctions: !functionsAreLexical))
            if (lexical.Contains(name)) throw Error("Declaration conflicts with a lexical declaration", start);
    }

    private void ValidateString(Token token)
    {
        if (_strict && token.HasLegacyOctalEscape) throw Error("Legacy numeric escape in strict mode", token);
    }

    private void ValidateTarget(Expr target, Token op)
    {
        if (target is not (IdentifierExpr or MemberExpr) ||
            _strict && target is IdentifierExpr { Name: "eval" or "arguments" })
            throw Error("Invalid assignment or update target", op);
    }

    private SourceSpan SpanFrom(SourcePosition start) => new(start, Previous.End);

    private static bool IsIdentifierName(Token token) => token.Kind is TokenKind.Identifier or TokenKind.Undefined ||
        token.Kind is >= TokenKind.Let and <= TokenKind.This ||
        token.Kind is >= TokenKind.Typeof and <= TokenKind.Finally ||
        token.Kind is TokenKind.Delete or TokenKind.Instanceof or TokenKind.In or TokenKind.Do ||
        token.Kind is >= TokenKind.For and <= TokenKind.Continue;

    private static int Precedence(TokenKind kind) => kind switch
    {
        TokenKind.Comma => 0,
        TokenKind.Equal or TokenKind.PlusEqual or TokenKind.MinusEqual or TokenKind.StarEqual or TokenKind.SlashEqual or TokenKind.PercentEqual or
            TokenKind.BitwiseAndEqual or TokenKind.BitwiseOrEqual or TokenKind.BitwiseXorEqual or
            TokenKind.ShiftLeftEqual or TokenKind.ShiftRightEqual or TokenKind.ShiftUnsignedRightEqual or TokenKind.PowerEqual or
            TokenKind.AndAndEqual or TokenKind.OrOrEqual or TokenKind.NullishAssign => 1,
        TokenKind.OrOr or TokenKind.NullishCoalescing => 3,
        TokenKind.AndAnd => 4,
        TokenKind.BitwiseOr => 5,
        TokenKind.BitwiseXor => 6,
        TokenKind.BitwiseAnd => 7,
        TokenKind.EqualEqual or TokenKind.StrictEqual or TokenKind.NotEqual or TokenKind.StrictNotEqual => 8,
        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual or TokenKind.Instanceof or TokenKind.In => 9,
        TokenKind.ShiftLeft or TokenKind.ShiftRight or TokenKind.ShiftUnsignedRight => 10,
        TokenKind.Plus or TokenKind.Minus => 11,
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => 12,
        TokenKind.Power => 13,
        _ => -1
    };

    private static bool MixesNullishAndLogical(TokenKind op, Expr operand) =>
        operand is BinaryExpr { IsParenthesized: false } binary &&
        (op == TokenKind.NullishCoalescing && binary.Op is TokenKind.AndAnd or TokenKind.OrOr ||
         op is TokenKind.AndAnd or TokenKind.OrOr && binary.Op == TokenKind.NullishCoalescing);

    private static TokenKind UnderlyingOperator(TokenKind kind) => kind switch
    {
        TokenKind.PlusEqual => TokenKind.Plus,
        TokenKind.MinusEqual => TokenKind.Minus,
        TokenKind.StarEqual => TokenKind.Star,
        TokenKind.SlashEqual => TokenKind.Slash,
        TokenKind.PercentEqual => TokenKind.Percent,
        TokenKind.BitwiseAndEqual => TokenKind.BitwiseAnd,
        TokenKind.BitwiseOrEqual => TokenKind.BitwiseOr,
        TokenKind.BitwiseXorEqual => TokenKind.BitwiseXor,
        TokenKind.ShiftLeftEqual => TokenKind.ShiftLeft,
        TokenKind.ShiftRightEqual => TokenKind.ShiftRight,
        TokenKind.ShiftUnsignedRightEqual => TokenKind.ShiftUnsignedRight,
        TokenKind.PowerEqual => TokenKind.Power,
        TokenKind.AndAndEqual => TokenKind.AndAnd,
        TokenKind.OrOrEqual => TokenKind.OrOr,
        TokenKind.NullishAssign => TokenKind.NullishCoalescing,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static JsSyntaxException Error(string message, Token token) => Error(message, token.Start);

    private static JsSyntaxException Error(string message, SourcePosition position) => new(message, position.Line, position.Column)
    {
        Offset = position.Offset
    };
}
