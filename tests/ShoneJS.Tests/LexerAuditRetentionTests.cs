// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Reflection;
using ShoneJs.Parsing;

namespace ShoneJs.Tests;

public class LexerAuditRetentionTests
{
    [Fact]
    public void OrdinaryParsingKeepsASmallTokenBuffer()
    {
        const int statementCount = 20_000;
        var source = string.Concat(Enumerable.Repeat("value;\r\n", statementCount));
        var lexer = ParserLexer(source);

        var statements = new Parser(lexer).Parse();

        Assert.Equal(statementCount, statements.Count);
        Assert.Equal(new SourcePosition(source.Length - 3, statementCount, 6),
            Assert.IsType<ExprStmt>(statements[^1]).Expression.Span.End);
        AssertSmallBuffer(lexer);
    }

    [Fact]
    public void ArrowRollbackReplaysMoreThanABufferOfTokens()
    {
        var group = "(" + string.Join(",", Enumerable.Repeat("x", 2_000)) + ")";
        var source = "value;" + group + ";";
        var lexer = ParserLexer(source);

        var statements = new Parser(lexer, typeScript: true).ParseTypeScript();

        var sequence = Assert.IsType<SequenceExpr>(Assert.IsType<ExprStmt>(statements[1]).Expression);
        Assert.Equal(2_000, sequence.Expressions.Count);
        Assert.Equal(group, source[sequence.Span.Start.Offset..sequence.Span.End.Offset]);
        AssertSmallBuffer(lexer);
    }

    [Fact]
    public void GenericCallRewindSurvivesNestedSpeculationAndRegexp()
    {
        var arguments = string.Concat(Enumerable.Repeat("x / 2,", 1_000)) + "/[}]/";
        var expression = "`head${outer<T>(inner<T>(" + arguments + "))}tail`;";
        var lexer = ParserLexer(expression);
        var statement = Assert.Single(new Parser(lexer, typeScript: true).ParseTypeScript());
        var template = Assert.IsType<TemplateExpr>(Assert.IsType<ExprStmt>(statement).Expression);
        var outer = Assert.IsType<CallExpr>(Assert.Single(template.Expressions));
        var inner = Assert.IsType<CallExpr>(Assert.Single(outer.Arguments));
        Assert.Equal(1_001, inner.Arguments.Count);
        Assert.Equal("[}]", Assert.IsType<RegExpLiteralExpr>(inner.Arguments[^1]).Pattern);
        Assert.Equal("tail", template.Quasis[^1].Cooked);
        AssertSmallBuffer(lexer);
        var slashSnapshots = InternalField<Dictionary<int, int[]>>(lexer, "_slashTemplates");
        Assert.Empty(slashSnapshots);
        Assert.InRange(slashSnapshots.EnsureCapacity(0), 0, 128);

        var invalid = "`head${outer<T>(inner<T>(" + arguments + "), ;)}tail`;";
        var expected = Assert.Throws<JsSyntaxException>(() => new Parser(new Lexer(invalid), typeScript: true).ParseTypeScript());
        var buffered = Assert.Throws<JsSyntaxException>(() => new Parser(ParserLexer(invalid), typeScript: true).ParseTypeScript());
        Assert.Equal(expected.Message, buffered.Message);
        Assert.Equal(expected.Offset, buffered.Offset);
    }

    [Fact]
    public void TemplateSlashSnapshotsExpireAndNestedRegexpRestoresTemplateState()
    {
        var divisions = string.Concat(Enumerable.Repeat("n = 16 / 2;\n", 1_000));
        var source = "let n; `head${(() => {" + divisions + "return `${/=[}]/g.test('=}')}:${n}`; })()}tail`;";
        var lexer = ParserLexer(source);
        Assert.Equal(2, new Parser(lexer).Parse().Count);
        AssertSmallBuffer(lexer);
        Assert.Empty(InternalField<Dictionary<int, int[]>>(lexer, "_slashTemplates"));
        Assert.Equal("headtrue:8tail", new JsEngine().Evaluate(source).ToString());
    }

    [Fact]
    public void PublicScanReusesItsCompleteListIncludingWhenCalledAfterParsing()
    {
        var lexer = new Lexer("if (true) /x/.test('x');\r\nvalue / 2;");
        var tokens = lexer.Scan();
        Assert.Same(tokens, lexer.Scan());
        Assert.Single(tokens, token => token.Kind == TokenKind.RegExp);
        Assert.Single(tokens, token => token.Kind == TokenKind.Slash);
        Assert.Equal(TokenKind.End, tokens[^1].Kind);
        Assert.Equal(2, new Parser(tokens).Parse().Count);
        Assert.Same(tokens, lexer.Scan());

        var sharedLexer = new Lexer("function f() {} /x/.test('x');");
        Assert.Equal(2, new Parser(sharedLexer).Parse().Count);
        var parserTokens = sharedLexer.Scan();
        Assert.Same(parserTokens, sharedLexer.Scan());
        Assert.Single(parserTokens, token => token.Kind == TokenKind.RegExp);
    }

    [Fact]
    public void EngineSourceLimitsAndLexerCancellationStillApply()
    {
        var limited = new JsEngine(new JsEngineOptions { MaximumSourceLength = 2 });
        Assert.Throws<InvalidOperationException>(() => limited.ParseScript("123"));
        Assert.Throws<InvalidOperationException>(() => limited.ParseModule("123"));
        Assert.Throws<InvalidOperationException>(() => limited.ParseTypeScript("123"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var lexer = ParserLexer("/*" + new string('x', 4_096) + "*/ 42;", cancellation.Token);
        Assert.Throws<OperationCanceledException>(() => new Parser(lexer).Parse());
    }

    // Inspect private storage to catch regressions without adding production instrumentation.
    private static Lexer ParserLexer(string source, CancellationToken cancellationToken = default) =>
        (Lexer)typeof(Lexer).GetMethod("CreateForParser", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [source, cancellationToken])!;

    private static T InternalField<T>(Lexer lexer, string name) =>
        (T)typeof(Lexer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lexer)!;

    private static void AssertSmallBuffer(Lexer lexer)
    {
        var tokens = InternalField<List<Token>>(lexer, "_tokens");
        Assert.InRange(tokens.Count, 1, 66);
        Assert.InRange(tokens.Capacity, 1, 128);
    }
}
