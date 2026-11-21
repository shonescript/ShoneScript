// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneJs.Parsing;

public sealed partial class Parser
{
    // TypeScript declarations are syntax-only AST nodes; they do not become executable statements.
    private Stmt? TypeScriptStatement()
    {
        if (!typeScript) return null;
        var start = Current;
        if (Current.Kind == TokenKind.Const && PeekToken(1).Text == "enum")
        {
            Take();
            Take();
            throw Error("const enum is not executable in this TypeScript mode", start);
        }
        if (Current.Kind != TokenKind.Identifier) return null;
        if (Current.Text == "type" && PeekToken(1).Kind == TokenKind.Identifier)
        {
            Take();
            var name = Need(TokenKind.Identifier);
            var typeParameters = Current.Kind == TokenKind.Less ? ParseTypeParameters() : [];
            Need(TokenKind.Equal);
            var type = ParseType();
            Match(TokenKind.Semicolon);
            return new TypeAliasStmt(name.Text, type)
            {
                TypeParameters = typeParameters,
                Span = new(start.Start, Previous.End)
            };
        }
        if (Current.Text == "interface" && PeekToken(1).Kind == TokenKind.Identifier)
        {
            Take();
            var name = Need(TokenKind.Identifier);
            var typeParameters = Current.Kind == TokenKind.Less ? ParseTypeParameters() : [];
            var baseTypes = new List<TypeNode>();
            if (Current.Kind == TokenKind.Identifier && Current.Text == "extends")
            {
                Take();
                do { baseTypes.Add(ParsePrimaryType()); } while (Match(TokenKind.Comma));
            }
            Need(TokenKind.LeftBrace);
            var members = new List<TypeMember>();
            while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.End)
            {
                var member = Need(TokenKind.Identifier);
                var optional = Match(TokenKind.Question);
                Need(TokenKind.Colon);
                members.Add(new TypeMember(member.Text, ParseType(), optional));
                if (!Match(TokenKind.Semicolon) && !Match(TokenKind.Comma) && Current.Kind != TokenKind.RightBrace)
                    throw Error("Expected type member separator", Current);
            }
            Need(TokenKind.RightBrace);
            Match(TokenKind.Semicolon);
            return new InterfaceStmt(name.Text, members)
            {
                TypeParameters = typeParameters,
                BaseTypes = baseTypes,
                Span = new(start.Start, Previous.End)
            };
        }
        if (Current.Text == "enum" && PeekToken(1).Kind == TokenKind.Identifier)
        {
            Take();
            var name = Need(TokenKind.Identifier);
            Need(TokenKind.LeftBrace);
            var members = new List<EnumMember>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.End)
            {
                var member = Take();
                if (member.Kind is not (TokenKind.Identifier or TokenKind.String or TokenKind.Number))
                    throw Error("Expected enum member name", member);
                var memberName = member.Text;
                if (!names.Add(memberName)) throw Error("Duplicate enum member", member);
                var initializer = Match(TokenKind.Equal) ? Expression(1) : null;
                members.Add(new EnumMember(memberName, initializer));
                if (!Match(TokenKind.Comma) && Current.Kind != TokenKind.RightBrace)
                    throw Error("Expected comma between enum members", Current);
            }
            Need(TokenKind.RightBrace);
            Match(TokenKind.Semicolon);
            return new EnumStmt(name.Text, members) { Span = new(start.Start, Previous.End) };
        }
        return null;
    }

    private Token PeekToken(int distance) => TokenAt(_index + distance);

    // Only the parameter head is speculative. Errors after '=>' belong to the arrow body.
    private bool TryArrow(out Expr arrow)
    {
        arrow = null!;
        var start = Current;
        var isAsync = start.Kind == TokenKind.Identifier && start.Text == "async" &&
            PeekToken(1).Kind is TokenKind.Identifier or TokenKind.LeftParen && !PeekToken(1).HasLineTerminatorBefore;
        if (start.Kind == TokenKind.Identifier && PeekToken(1).Kind == TokenKind.Arrow && !PeekToken(1).HasLineTerminatorBefore)
        {
            Take();
            Take();
            arrow = ParseArrowBody([new ArrowParameter(start.Text)], [start], null, start.Start, false);
            return true;
        }
        if (!isAsync && start.Kind != TokenKind.LeftParen && !(typeScript && start.Kind == TokenKind.Less)) return false;
        var saved = _index;
        var savedPending = _pendingTypeClosers;
        List<ArrowParameter> parameters = [];
        List<Token> names = [];
        List<TypeParameter> typeParameters = [];
        TypeNode? returnType = null;
        try
        {
            if (isAsync) Take();
            if (isAsync && Current.Kind == TokenKind.Identifier)
            {
                var name = Take();
                names.Add(name);
                parameters.Add(new ArrowParameter(name.Text));
            }
            else
            {
                if (typeScript && Current.Kind == TokenKind.Less) typeParameters = ParseTypeParameters();
                if (_pendingTypeClosers != 0) throw Error("Unmatched generic type closer", Current);
                Need(TokenKind.LeftParen);
                while (Current.Kind != TokenKind.RightParen)
                {
                    var parameter = Need(TokenKind.Identifier);
                    names.Add(parameter);
                    if (typeScript) Match(TokenKind.Question);
                    var type = typeScript && Match(TokenKind.Colon) ? ParseType() : null;
                    parameters.Add(new ArrowParameter(parameter.Text, type));
                    if (!Match(TokenKind.Comma)) break;
                }
                Need(TokenKind.RightParen);
                if (typeScript && Match(TokenKind.Colon)) returnType = ParseType();
            }
            if (LineBreak) throw Error("Line terminator before arrow", Current);
            Need(TokenKind.Arrow);
        }
        catch (JsSyntaxException)
        {
            _index = saved;
            _pendingTypeClosers = savedPending;
            return false;
        }
        arrow = ParseArrowBody(parameters, names, returnType, start.Start, isAsync) with { TypeParameters = typeParameters };
        return true;
    }

    private ArrowFunctionExpr ParseArrowBody(IReadOnlyList<ArrowParameter> parameters, List<Token> names,
        TypeNode? returnType, SourcePosition start, bool isAsync)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            ValidateBinding(name);
            if (isAsync && name.Text == "await") throw Error("Invalid async parameter", name);
            if (!seen.Add(name.Text)) throw Error("Duplicate arrow parameter", name);
        }
        if (Current.Kind == TokenKind.LeftBrace)
        {
            var body = FunctionBody(null, names, isAsync);
            return new ArrowFunctionExpr(parameters, null, body) { IsAsync = isAsync, ReturnType = returnType, Span = new(start, body.Span.End) };
        }
        var previousAsync = _asyncFunction;
        _asyncFunction = isAsync;
        _functionDepth++;
        try
        {
            var expression = Expression(1);
            return new ArrowFunctionExpr(parameters, expression, null) { IsAsync = isAsync, ReturnType = returnType, Span = new(start, expression.Span.End) };
        }
        finally { _functionDepth--; _asyncFunction = previousAsync; }
    }

    // In TSX-like syntax, '<...>' can be a comparison rather than type arguments; rollback
    // keeps ordinary JavaScript expressions intact when the call shape does not match.
    private bool TryGenericCall(Expr target, out Expr call)
    {
        var saved = _index;
        var savedPendingClosers = _pendingTypeClosers;
        try
        {
            Need(TokenKind.Less);
            var typeArguments = new List<TypeNode>();
            do { typeArguments.Add(ParseType()); } while (_pendingTypeClosers == 0 && Match(TokenKind.Comma));
            NeedTypeGreater();
            if (_pendingTypeClosers != 0 || Current.Kind != TokenKind.LeftParen)
            {
                _index = saved;
                _pendingTypeClosers = savedPendingClosers;
                call = target;
                return false;
            }
            call = new CallExpr(target, Arguments())
            {
                TypeArguments = typeArguments,
                Span = new(target.Span.Start, Previous.End)
            };
            return true;
        }
        catch (JsSyntaxException)
        {
            _index = saved;
            _pendingTypeClosers = savedPendingClosers;
            call = target;
            return false;
        }
    }

    private List<TypeParameter> ParseTypeParameters()
    {
        Need(TokenKind.Less);
        var parameters = new List<TypeParameter>();
        do
        {
            var name = Need(TokenKind.Identifier);
            var constraint = Current.Kind == TokenKind.Identifier && Current.Text == "extends"
                ? TakeAndParseType() : null;
            var defaultType = Match(TokenKind.Equal) ? ParseType() : null;
            parameters.Add(new TypeParameter(name.Text, constraint, defaultType));
        } while (Match(TokenKind.Comma));
        NeedTypeGreater();
        return parameters;
    }

    private TypeNode TakeAndParseType()
    {
        Take();
        return ParseType();
    }

    // The lexer emits >> and >>> as shift tokens. Type parsing splits those tokens virtually
    // into multiple generic '>' closers without changing the shared token stream.
    private void NeedTypeGreater()
    {
        if (_pendingTypeClosers > 0)
        {
            _pendingTypeClosers--;
            return;
        }
        switch (Current.Kind)
        {
            case TokenKind.Greater:
                Take();
                return;
            case TokenKind.ShiftRight:
                Take();
                _pendingTypeClosers = 1;
                return;
            case TokenKind.ShiftUnsignedRight:
                Take();
                _pendingTypeClosers = 2;
                return;
            default:
                throw Error("Expected >", Current);
        }
    }

    private TypeNode ParseType()
    {
        EnterParse();
        try { return ParseUnionType(); }
        finally { _parseDepth--; }
    }

    private TypeNode ParseUnionType()
    {
        var first = ParseIntersectionType();
        if (_pendingTypeClosers != 0 || Current.Kind != TokenKind.BitwiseOr) return first;
        var types = new List<TypeNode> { first };
        while (_pendingTypeClosers == 0 && Match(TokenKind.BitwiseOr)) types.Add(ParseIntersectionType());
        return new UnionTypeNode(types) { Span = new(first.Span.Start, types[^1].Span.End) };
    }

    private TypeNode ParseIntersectionType()
    {
        var first = ParsePrimaryType();
        if (_pendingTypeClosers != 0 || Current.Kind != TokenKind.BitwiseAnd) return first;
        var types = new List<TypeNode> { first };
        while (_pendingTypeClosers == 0 && Match(TokenKind.BitwiseAnd)) types.Add(ParsePrimaryType());
        return new IntersectionTypeNode(types) { Span = new(first.Span.Start, types[^1].Span.End) };
    }

    private TypeNode ParsePrimaryType()
    {
        var type = ParseTypeAtom();
        while (_pendingTypeClosers == 0 && !LineBreak && Current.Kind == TokenKind.LeftBracket && PeekToken(1).Kind == TokenKind.RightBracket)
        {
            Take();
            Take();
            type = new ArrayTypeNode(type) { Span = new(type.Span.Start, Previous.End) };
        }
        return type;
    }

    private TypeAssertionExpr ParseAngleAssertion()
    {
        var type = ParseType();
        NeedTypeGreater();
        if (_pendingTypeClosers != 0) throw Error("Unmatched generic type closer", Current);
        return new TypeAssertionExpr(Expression(14), type);
    }

    private TypeNode ParseTypeAtom()
    {
        var start = Current;
        if (Match(TokenKind.LeftParen))
        {
            var inner = ParseType();
            Need(TokenKind.RightParen);
            return new ParenthesizedTypeNode(inner) { Span = new(start.Start, Previous.End) };
        }
        if (Match(TokenKind.LeftBracket))
        {
            var elements = new List<TypeNode>();
            while (Current.Kind != TokenKind.RightBracket)
            {
                elements.Add(ParseType());
                if (!Match(TokenKind.Comma)) break;
            }
            Need(TokenKind.RightBracket);
            return new TupleTypeNode(elements) { Span = new(start.Start, Previous.End) };
        }
        if (Match(TokenKind.LeftBrace))
        {
            var members = new List<TypeMember>();
            while (Current.Kind != TokenKind.RightBrace && Current.Kind != TokenKind.End)
            {
                var key = Need(TokenKind.Identifier);
                var optional = Match(TokenKind.Question);
                Need(TokenKind.Colon);
                members.Add(new TypeMember(key.Text, ParseType(), optional));
                if (!Match(TokenKind.Semicolon) && !Match(TokenKind.Comma) && Current.Kind != TokenKind.RightBrace)
                    throw Error("Expected type member separator", Current);
            }
            Need(TokenKind.RightBrace);
            return new ObjectTypeNode(members) { Span = new(start.Start, Previous.End) };
        }
        if (Current.Kind is not (TokenKind.Identifier or TokenKind.String or TokenKind.Number))
            throw Error("Expected type", Current);
        var name = Take();
        List<TypeNode>? arguments = null;
        if (Match(TokenKind.Less))
        {
            arguments = [];
            do { arguments.Add(ParseType()); } while (_pendingTypeClosers == 0 && Match(TokenKind.Comma));
            NeedTypeGreater();
        }
        return new NamedTypeNode(name.Text, arguments) { Span = new(start.Start, Previous.End) };
    }
}
