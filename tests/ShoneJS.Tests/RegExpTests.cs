// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneJs.Tests;

public class RegExpTests
{
    [Fact]
    public void SlashContextUsesTheGrammarInsteadOfOnlyThePreviousToken()
    {
        const string source = """
            let n = 36; n /= 3; n = n / 2 / 3;
            if (n === 2) /[\/]/.test('/') && (n += 1);
            function first() { return /=a/.test('=a'); }
            function statement() {}
            /\d/.test('1') && first() && n === 3 && (function () { return 8; })() / 2 === 4;
            """;
        Assert.Equal("true", new JsEngine().Evaluate(source).ToString());
        Assert.Equal("true", new JsEngine().Evaluate("""`${/[}]/.test('}')}` === 'true' && 8 / /x/.test('x') === 8""").ToString());
    }

    [Fact]
    public void StandaloneTokensAndAstPreservePatternFlagsAndSpan()
    {
        const string source = "let r = /a[\\/]b/mi; if (true) r / 2; ({ default: 6 }).default / 2;";
        var tokens = new Lexer(source).Scan();
        var regex = Assert.Single(tokens, token => token.Kind == TokenKind.RegExp);
        Assert.Equal(@"a[\/]b", regex.Text);
        Assert.Equal("mi", regex.RegExpFlags);
        Assert.Equal(@"/a[\/]b/mi", regex.Raw);
        Assert.Contains(tokens, token => token.Kind == TokenKind.Slash);
        var statements = new Parser(tokens).Parse();
        var ast = Assert.IsType<RegExpLiteralExpr>(Assert.IsType<VarStmt>(statements[0]).Init);
        Assert.Equal(regex.Span, ast.Span);
        Assert.Equal(regex.Text, ast.Pattern);
        Assert.Equal(regex.RegExpFlags, ast.Flags);
    }

    [Theory]
    [InlineData("/unterminated")]
    [InlineData("/[a/")]
    [InlineData("/a\\\nb/")]
    [InlineData("/a/gg")]
    [InlineData("/a/u")]
    [InlineData("/(?i:a)/")]
    [InlineData("/(a)\\1/")]
    [InlineData("/(/")]
    public void LiteralInvalidityIsAnEarlyError(string expression)
    {
        var error = Assert.Throws<JsSyntaxException>(() => new JsEngine().ParseScript("function f() { return " + expression + "; }"));
        Assert.Equal(22, error.Offset);
    }

    [Fact]
    public void ConstructorsAndLiteralsHaveIndependentStateAndRealmPrototype()
    {
        Assert.Equal("true", new JsEngine().Evaluate("""
            function literal() { return /x/yg; }
            let a = literal(), b = literal(), c = new RegExp(a);
            a.lastIndex = 1;
            a !== b && c !== a && RegExp(a) === a && RegExp(a, 'i') !== a && c.lastIndex === 0 &&
              a instanceof RegExp && a.flags === 'gy' && a.sticky && a.global && !a.ignoreCase &&
              RegExp().source === '(?:)' && /[/]/.source === '[/]' && String(RegExp('a/b', 'i')) === '/a\\/b/i';
            """).ToString());
        Assert.Equal("SyntaxError", new JsEngine().Evaluate("try { RegExp('x', 'uu'); } catch (e) { e.name; }").ToString());
    }

    [Fact]
    public void ExecAndStickyUseUtf16IndicesAndPresentOptionalCaptures()
    {
        Assert.Equal("true", new JsEngine().Evaluate("""
            let r = /(a)(b)?/gy;
            r.lastIndex = 2;
            let m = r.exec('😃a');
            let good = m instanceof Array && m[0] === 'a' && m[1] === 'a' && m[2] === undefined &&
                m.input === '😃a' && m.index === 2 && m.groups === undefined && r.lastIndex === 3;
            let miss = r.exec('😃a') === null && r.lastIndex === 0;
            let sticky = r.exec('ba') === null && r.lastIndex === 0;
            let empty = /(?:)/g; empty.lastIndex = 1;
            let plain = /a/; plain.lastIndex = 7;
            good && miss && sticky && empty.exec('a').index === 1 && empty.lastIndex === 1 &&
                plain.test('a') && plain.lastIndex === 7;
            """).ToString());
    }

    [Fact]
    public void JavaScriptAnchorsWhitespaceAndDotUseEcmaLineTerminators()
    {
        Assert.Equal("true", new JsEngine().Evaluate("""
            !/x$/.test('x\n') && /^x$/m.test('a\rx\u2028b') && !/./.test('\r') && /./s.test('\u2029') &&
                /\s/.test('\uFEFF') && !/\s/.test('\u0085') && /[\s\S]/.test('\r') && /[^]/.test('\u2028') &&
                /\a/.test('a') && !/[]/.test(']');
            """).ToString());
    }

    [Fact]
    public void StringMatchUsesCaptureResultsOrWholeGlobalMatchesAndAdvancesEmptyMatches()
    {
        Assert.Equal("true", new JsEngine().Evaluate("""
            let one = 'xab'.match('(a)(b)');
            let rx = /a/g; rx.lastIndex = 100;
            let many = 'aba'.match(rx);
            let empty = 'ab'.match(/(?:)/g);
            one[0] === 'ab' && one[1] === 'a' && one[2] === 'b' && one.index === 1 &&
               many.length === 2 && many.index === undefined && rx.lastIndex === 0 &&
               empty.length === 3 && 'ab'.match(/z/g) === null && 'x'.match()[0] === '';
            """).ToString());
    }

    [Fact]
    public void StringReplaceSupportsLiteralSearchTemplatesAndCallbacks()
    {
        var engine = new JsEngine();
        Assert.Equal("a$-b-a-cc", engine.Evaluate("""'abc'.replace('b', "$$-$&-$`-$'")""").ToString());
        Assert.Equal("a_b_a_", engine.Evaluate("""'aba'.replace(/(?=.)|$/g, (_, i) => i === 0 ? '' : '_')""").ToString());
        Assert.Equal("ba-a0-ba-$9", engine.Evaluate("""'ab'.replace(/(a)(b)/, '$2$1-$10-$02$1-$9')""").ToString());
        Assert.Equal("true", engine.Evaluate("""
            let r = /(a)(b)?/g;
            let positions = '';
            let result = 'a ab'.replace(r, (whole, a, b, index, input) => {
                positions += index;
                return a + (b === undefined ? '-' : b) + r.lastIndex;
            });
            result === 'a-0 ab0' && positions === '02' && r.lastIndex === 0 && 'x..'.replace('.', '!') === 'x!.';
            """).ToString());
    }

    [Fact]
    public void BackendTimeoutAndReplacementAmplificationBecomeJavaScriptRangeErrors()
    {
        var engine = new JsEngine();
        engine.SetGlobal("input", JsValue.From(new string('a', 100_000) + "!"));
        Assert.Equal("RegExp match time limit exceeded", engine.Evaluate("try { /^(a+)+$/.test(input); } catch (e) { e.message; }").ToString());
        engine.SetGlobal("input", JsValue.From(new string('a', 1_700)));
        Assert.Equal("String.replace output length limit exceeded", engine.Evaluate("""try { input.replace(/a/g, "$'"); } catch (e) { e.message; }""").ToString());
    }

    [Fact]
    public void RegexCapsPatternInputAndCumulativeMatchesAndChargesNativeWork()
    {
        var engine = new JsEngine();
        engine.SetGlobal("longInput", JsValue.From(new string('a', 262_145)));
        engine.SetGlobal("manyMatches", JsValue.From(new string('a', 32_768)));
        engine.SetGlobal("longPattern", JsValue.From(new string('a', 4_097)));
        Assert.Equal("RangeError", engine.Evaluate("try { /x/.test(longInput); } catch (e) { e.name; }").ToString());
        Assert.Equal("RangeError", engine.Evaluate("try { manyMatches.match(/(?:)/g); } catch (e) { e.name; }").ToString());
        Assert.Equal("SyntaxError", engine.Evaluate("try { RegExp(longPattern); } catch (e) { e.name; }").ToString());
        engine.Options.MaximumExecutionSteps = 100;
        var error = Assert.Throws<InvalidOperationException>(() => engine.Evaluate("/x/.test(manyMatches)"));
        Assert.Contains("step limit", error.Message);
    }
}
