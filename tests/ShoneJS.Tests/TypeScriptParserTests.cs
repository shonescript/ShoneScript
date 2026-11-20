// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneScript.Parsing;

namespace ShoneScript.Tests;

public class TypeScriptParserTests
{
    private static List<Stmt> Parse(string source) => new Parser(new Lexer(source).Scan(), typeScript: true).ParseTypeScript();

    [Fact]
    public void PreservesTypeNodesAndOriginalSourcePositions()
    {
        const string source = "const data: (number | string)[] = [42];";
        var variable = Assert.IsType<VarStmt>(Assert.Single(Parse(source)));
        var array = Assert.IsType<ArrayTypeNode>(variable.TypeAnnotation);
        var parenthesized = Assert.IsType<ParenthesizedTypeNode>(array.Element);
        Assert.IsType<UnionTypeNode>(parenthesized.Inner);
        Assert.Equal("(number | string)[]", source[array.Span.Start.Offset..array.Span.End.Offset]);
    }

    [Fact]
    public void DistinguishesGenericClosersFromRelationalAndShiftOperators()
    {
        var statements = Parse("f<Array<Array<number>>>(value); a < b; a >> b;");
        var call = Assert.IsType<CallExpr>(Assert.IsType<ExprStmt>(statements[0]).Expression);
        Assert.IsType<NamedTypeNode>(Assert.Single(call.TypeArguments!));
        Assert.Equal(TokenKind.Less, Assert.IsType<BinaryExpr>(Assert.IsType<ExprStmt>(statements[1]).Expression).Op);
        Assert.Equal(TokenKind.ShiftRight, Assert.IsType<BinaryExpr>(Assert.IsType<ExprStmt>(statements[2]).Expression).Op);
    }

    [Fact]
    public void NewlineSeparatesNonNullAssertionFromPrefixNegation()
    {
        var statements = Parse("x\n!y");
        Assert.IsType<IdentifierExpr>(Assert.IsType<ExprStmt>(statements[0]).Expression);
        Assert.IsType<UnaryExpr>(Assert.IsType<ExprStmt>(statements[1]).Expression);
    }
}
