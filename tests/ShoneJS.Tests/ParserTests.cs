// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShoneJS.Parsing;

namespace ShoneJS.Tests;

public class ParserTests
{
    private static Parser ParserFor(string source, ParseGoal goal = ParseGoal.Script, bool forceStrict = false) =>
        new(new Lexer(source).Scan(), goal, forceStrict);

    [Theory]
    [InlineData("let a = 1 let b = 2")]
    [InlineData("a b")]
    [InlineData("if (a) b else c")]
    [InlineData("for (let i = 0\ni < 3; i++) {}")]
    public void RejectsMissingRequiredSemicolons(string source) =>
        Assert.Throws<JsSyntaxException>(() => ParserFor(source).Parse());

    [Fact]
    public void InsertsSemicolonsAtLineBreakCloseBraceAndEndOfInput()
    {
        var statements = ParserFor("let x = 1 /*\r\n*/ x\u2028{x} x").Parse();
        Assert.Collection(statements,
            statement => Assert.IsType<VarStmt>(statement),
            statement => Assert.IsType<ExprStmt>(statement),
            statement => Assert.IsType<ExprStmt>(Assert.Single(Assert.IsType<BlockStmt>(statement).Body)),
            statement => Assert.IsType<ExprStmt>(statement));
    }

    [Fact]
    public void ALineBreakDoesNotEndAStatementWhenTheExpressionCanContinue()
    {
        var statements = ParserFor("f\n(1)\n[0]\n+ 2").Parse();
        var expression = Assert.IsType<BinaryExpr>(Assert.IsType<ExprStmt>(Assert.Single(statements)).Expression);
        Assert.IsType<CallExpr>(Assert.IsType<MemberExpr>(expression.Left).Target);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void AppliesRestrictedProductionRulesAcrossLineTerminators(string lineBreak)
    {
        var fn = Assert.IsType<FunctionStmt>(Assert.Single(ParserFor($"function f() {{ return /*{lineBreak}*/ 1; }}").Parse()));
        Assert.Null(Assert.IsType<ReturnStmt>(fn.Body.Body[0]).Value);
        Assert.IsType<ExprStmt>(fn.Body.Body[1]);
        Assert.Throws<JsSyntaxException>(() => ParserFor($"throw /*{lineBreak}*/ 1").Parse());

        var updates = ParserFor($"x{lineBreak}++y").Parse();
        Assert.IsType<IdentifierExpr>(Assert.IsType<ExprStmt>(updates[0]).Expression);
        Assert.True(Assert.IsType<UpdateExpr>(Assert.IsType<ExprStmt>(updates[1]).Expression).Prefix);
    }

    [Fact]
    public void TracksTokenNodeAndErrorLocationsInUtf16Offsets()
    {
        const string source = "let x = 1;\r\n// comment\u2028x = '🌍';";
        var tokens = new Lexer(source).Scan();
        var statement = Assert.IsType<ExprStmt>(ParserFor(source).Parse()[1]);
        Assert.Equal(new SourcePosition(23, 3, 1), statement.Span.Start);
        Assert.Equal(new SourcePosition(source.Length, 3, 10), statement.Span.End);
        var stringToken = Assert.Single(tokens, token => token.Kind == TokenKind.String);
        Assert.Equal("'🌍'", stringToken.Raw);
        Assert.Equal(4, stringToken.End.Offset - stringToken.Start.Offset);
        Assert.Equal((2, 4, 8), Location(Assert.Throws<JsSyntaxException>(() => ParserFor("x =\r\n y @").Parse())));
    }

    private static (int Line, int Column, int Offset) Location(JsSyntaxException error) => (error.Line, error.Column, error.Offset);

    [Theory]
    [InlineData("return", "Return")]
    [InlineData("{ return 1; }", "Return")]
    [InlineData("(1 + 2) = 3;", "target")]
    [InlineData("f()++", "target")]
    [InlineData("++[]", "target")]
    [InlineData("function f() return 1;", "LeftBrace")]
    [InlineData("const x;", "initializer")]
    public void ReportsEarlyErrors(string source, string message) =>
        Assert.Contains(message, Assert.Throws<JsSyntaxException>(() => ParserFor(source).Parse()).Message);

    [Fact]
    public void RecognizesStrictDirectivesAndKeepsFunctionContextLocal()
    {
        var parser = ParserFor("function f() { 'directive'; 'use strict'; return function() {}; } function g(a, a) {}");
        var body = parser.Parse();
        var strictFunction = Assert.IsType<FunctionStmt>(body[0]);
        Assert.True(strictFunction.Body.IsStrict);
        var result = Assert.IsType<ReturnStmt>(strictFunction.Body.Body[2]);
        Assert.True(Assert.IsType<FunctionExpr>(result.Value).Body.IsStrict);
        Assert.False(Assert.IsType<FunctionStmt>(body[1]).Body.IsStrict);
        Assert.False(parser.IsStrict);
    }

    [Theory]
    [InlineData("function f(a, a) { 'use strict'; }")]
    [InlineData("function eval() { 'use strict'; }")]
    [InlineData("'use strict'; function f(arguments) {}")]
    [InlineData("'use strict'; eval += 1")]
    [InlineData("'use strict'; ++arguments")]
    [InlineData("'use strict'; delete (x)")]
    [InlineData("'use strict'; let x = 010")]
    [InlineData("'\\1'; 'use strict';")]
    public void RejectsStrictModeEarlyErrors(string source) =>
        Assert.Throws<JsSyntaxException>(() => ParserFor(source).Parse());

    [Theory]
    [InlineData("'use\\x20strict';")]
    [InlineData("('use strict');")]
    [InlineData("'use strict' + '';")]
    [InlineData("'use strict'\n+ '';")]
    [InlineData("'use strict'(1);")]
    [InlineData("{ 'use strict'; }")]
    [InlineData("; 'use strict';")]
    public void IgnoresStringsOutsideAnExactDirective(string precedingSource)
    {
        var parser = ParserFor(precedingSource + " function f(a, a) {}");
        parser.Parse();
        Assert.False(parser.IsStrict);
    }

    [Fact]
    public void ModuleAndForcedStrictGoalsApplyBeforeParsing()
    {
        var module = ParserFor("var x = 1", ParseGoal.Module);
        Assert.Equal(ParseGoal.Module, module.Goal);
        Assert.True(module.IsStrict);
        module.Parse();
        Assert.Throws<JsSyntaxException>(() => ParserFor("var await", ParseGoal.Module).Parse());
        Assert.Throws<JsSyntaxException>(() => ParserFor("function f(a, a) {}", ParseGoal.Module).Parse());
        Assert.Throws<JsSyntaxException>(() => ParserFor("var eval", forceStrict: true).Parse());
        Assert.Throws<JsSyntaxException>(() => ParserFor("function f() {} var f;", ParseGoal.Module).Parse());
        Assert.Single(ParserFor("var await").Parse());
    }

    [Fact]
    public void ParsesNestedTemplatesWithBalancedSubstitutionsAndCookedText()
    {
        const string source = "`prefix\\n${{x: `nested ${value}`}.x} / ${a, b} \\${literal}\r\n`";
        var template = Assert.IsType<TemplateExpr>(ParserFor(source).ParseExpression());
        Assert.Equal(["prefix\\n", " / ", " \\${literal}\n"], template.Quasis.Select(quasi => quasi.Raw));
        Assert.Equal(["prefix\n", " / ", " ${literal}\n"], template.Quasis.Select(quasi => quasi.Cooked));
        var member = Assert.IsType<MemberExpr>(template.Expressions[0]);
        var nested = Assert.IsType<TemplateExpr>(Assert.Single(Assert.IsType<ObjectExpr>(member.Target).Properties).Value);
        Assert.Equal("value", Assert.IsType<IdentifierExpr>(Assert.Single(nested.Expressions)).Name);
        Assert.IsType<SequenceExpr>(template.Expressions[1]);
        Assert.Equal(new SourcePosition(0, 1, 1), template.Span.Start);
        Assert.Equal(new SourcePosition(source.Length, 2, 2), template.Span.End);
    }

    [Theory]
    [InlineData("`unterminated")]
    [InlineData("`${x")]
    [InlineData("`${}`")]
    [InlineData("`${x y}`")]
    [InlineData("`\\8`")]
    public void ReportsMalformedTemplates(string source) =>
        Assert.Throws<JsSyntaxException>(() => ParserFor(source).Parse());

    [Fact]
    public void AcceptsHashbangOnlyAtTheStartOfSource()
    {
        var statement = Assert.IsType<ExprStmt>(Assert.Single(ParserFor("#!/usr/bin/env js\r\n1").Parse()));
        Assert.Equal(2, statement.Span.Start.Line);
        Assert.Throws<JsSyntaxException>(() => ParserFor("\n#!comment\n1").Parse());
        Assert.Throws<JsSyntaxException>(() => ParserFor("\uFEFF#!comment\n1").Parse());
    }

    [Theory]
    [InlineData("let x; const x = 0;")]
    [InlineData("let x; { var x; }")]
    [InlineData("{ let x; var x; }")]
    [InlineData("function f(x) { let x; }")]
    [InlineData("let x; function x() {}")]
    [InlineData("switch (x) { case 1: let y; case 2: let y; }")]
    [InlineData("for (let x = 0; x < 1; x++) { var x; }")]
    [InlineData("try {} catch (x) { let x; }")]
    [InlineData("'use strict'; { function f() {} function f() {} }")]
    public void RejectsConflictingLexicalDeclarations(string source) =>
        Assert.Throws<JsSyntaxException>(() => ParserFor(source).Parse());

    [Fact]
    public void LexicalDeclarationsCanShadowOuterBindings()
    {
        Assert.Equal(2, ParserFor("var x; { let x; function f() { var x; } }").Parse().Count);
        Assert.Single(ParserFor("function f(x) { { let x; } var x; }").Parse());
    }

    [Fact]
    public void PreservesOptionalChainBoundariesAndReceiver()
    {
        var chain = Assert.IsType<OptionalChainExpr>(ParserFor("obj.method?.().value?.[key]").ParseExpression());
        Assert.IsType<MemberExpr>(chain.Target);
        Assert.Collection(chain.Segments,
            segment => Assert.True(Assert.IsType<ChainCall>(segment).Optional),
            segment => Assert.False(Assert.IsType<ChainMember>(segment).Optional),
            segment => Assert.True(Assert.IsType<ChainMember>(segment).Optional));
        var outer = Assert.IsType<CallExpr>(ParserFor("(obj?.method)()").ParseExpression());
        Assert.IsType<OptionalChainExpr>(outer.Target);
        var nullish = Assert.IsType<BinaryExpr>(ParserFor("a ?? (b && c)").ParseExpression());
        Assert.Equal(TokenKind.NullishCoalescing, nullish.Op);
        Assert.IsType<ConditionalExpr>(ParserFor("a?.1:2").ParseExpression());
    }

    [Theory]
    [InlineData("a?.x = 1")]
    [InlineData("a?.x++")]
    [InlineData("new a?.x()")]
    [InlineData("a ?? b && c")]
    [InlineData("a && b ?? c")]
    [InlineData("a ?? b || c")]
    [InlineData("a || b ?? c")]
    [InlineData("a?.b ??= c")]
    public void RejectsInvalidOptionalAndNullishSyntax(string source) =>
        Assert.Throws<JsSyntaxException>(() => ParserFor(source).ParseExpression());

    [Fact]
    public void ParsesHarnessConstructs()
    {
        const string source = "var x = 0, y = 1; for (var i = 0; i < 2; i++) { " +
            "switch (i) { case 0: x += y; break; default: continue; } } " +
            "try { throw new TypeError(typeof x); } catch (e) { e instanceof TypeError; } finally { void x; }";
        var statements = ParserFor(source).Parse();
        Assert.Equal(2, Assert.IsType<VarListStmt>(statements[0]).Declarations.Count);
        var iteration = Assert.IsType<ForStmt>(statements[1]);
        Assert.IsType<UpdateExpr>(iteration.Update);
        var selection = Assert.IsType<SwitchStmt>(Assert.Single(Assert.IsType<BlockStmt>(iteration.Body).Body));
        Assert.IsType<CompoundAssignExpr>(Assert.IsType<ExprStmt>(selection.Cases[0].Body[0]).Expression);
        var handling = Assert.IsType<TryStmt>(statements[2]);
        var construction = Assert.IsType<NewExpr>(Assert.IsType<ThrowStmt>(Assert.Single(handling.Body.Body)).Value);
        Assert.Equal(TokenKind.Typeof, Assert.IsType<UnaryExpr>(Assert.Single(construction.Arguments)).Op);
        Assert.Equal("e", handling.CatchName);
        Assert.Equal(TokenKind.Instanceof,
            Assert.IsType<BinaryExpr>(Assert.IsType<ExprStmt>(Assert.Single(handling.CatchBody!.Body)).Expression).Op);
        Assert.Equal(new BigInt(255), Assert.IsType<BigIntExpr>(ParserFor("0xF_Fn").ParseExpression()).Value);
    }
}
