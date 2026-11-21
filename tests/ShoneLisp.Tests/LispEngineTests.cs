// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using ShoneJs.Parsing;
using ShoneLisp.Parsing;
using ShoneLisp.Runtime;

namespace ShoneLisp.Tests;

public sealed class LispEngineTests
{
    [Fact]
    public void ReaderHandlesCommentsEscapesAndQuotingWithoutEvaluatingData()
    {
        var engine = new LispEngine();
        var value = engine.Execute("""
            ; a leading comment
            '(outer (unknown #f 2.5) () "a;b\n\"c" 'inner) ; a trailing comment
            """);

        Assert.Equal("(outer (unknown #f 2.5) () \"a;b\\n\\\"c\" (quote inner))", value.ToString());
        Assert.Equal("outer", value.Car.AsSymbol());
        Assert.Equal("inner", engine.Execute("(quote inner)").AsSymbol());
        Assert.Same(LispValue.EmptyList, engine.Execute("'()"));
        Assert.Equal("(a b . c)", engine.Execute("'(a . (b . c))").ToString());
    }

    [Fact]
    public void LexicalClosuresCaptureMutableBindingsAndKeepEachFactoryCallIndependent()
    {
        var engine = new LispEngine();
        var result = engine.Execute("""
            (define (counter start)
              (lambda () (set! start (+ start 1)) start))
            (define a (counter 10))
            (define b (counter 100))
            (let ((start 900)) (list (a) (b) (a) start))
            """);

        Assert.Equal("(11 101 12 900)", result.ToString());
    }

    [Fact]
    public void LetUsesParallelInitializersAndSetUpdatesNearestBinding()
    {
        var engine = new LispEngine();
        Assert.Equal("(2 10 10)", engine.Execute("""
            (define x 10)
            (let ((x 1) (y x))
              (begin
                (set! x (+ x 1))
                (list x y (let () y))))
            """).ToString());
        Assert.Equal(10, engine.GetGlobal("x").AsNumber());
        Assert.Throws<LispRuntimeException>(() => engine.Execute("(set! unbound 1)"));
    }

    [Fact]
    public void DefinitionsSupportSelfAndMutualRecursion()
    {
        var engine = new LispEngine();
        Assert.Equal("(120 #t #f)", engine.Execute("""
            (define (factorial n)
              (if (= n 0) 1 (* n (factorial (- n 1)))))
            (define even? (lambda (n) (if (= n 0) #t (odd? (- n 1)))))
            (define odd? (lambda (n) (if (= n 0) #f (even? (- n 1)))))
            (list (factorial 5) (even? 8) (odd? 8))
            """).ToString());
    }

    [Fact]
    public void OnlyFalseIsFalseAndIfDoesNotEvaluateTheUnusedBranch()
    {
        var engine = new LispEngine();
        Assert.Equal("(1 2 3 4 5)", engine.Execute("""
            (list (if #f missing 1) (if #t 2 missing) (if 0 3 missing)
                  (if '() 4 missing) (if "" 5 missing))
            """).ToString());
        Assert.True(engine.Execute("(if #f missing)").IsUnspecified);
        Assert.True(engine.Execute("(begin)").IsUnspecified);
        Assert.True(engine.Execute("; empty script").IsUnspecified);
    }

    [Theory]
    [InlineData("(list (+) (*) (- 2) (/ 2) (+ -1.5 .5 +2e1))", "(0 1 -2 0.5 19)")]
    [InlineData("(list (< 1 2 3) (<= 3 3) (> 2 3) (>= 3 3) (= 1 1.0) (not 0))", "(#t #t #f #t #t #f)")]
    [InlineData("(list (car '(a b)) (cdr '(a b)) (cons 'a 'b) (length '(1 2 3)) (null? (list)))", "(a (b) (a . b) 3 #t)")]
    [InlineData("(list (list? '(a . b)) (pair? '(a . b)) (append '(1 2) '(3) 4))", "(#f #t (1 2 3 . 4))")]
    [InlineData("(list (eq? 'x 'x) (equal? '(x \"y\") (list 'x \"y\")) (equal? '(x) '(y)))", "(#t #t #f)")]
    public void CoreBuiltinsReturnExpectedValues(string source, string expected) =>
        Assert.Equal(expected, new LispEngine().Execute(source).ToString());

    [Theory]
    [InlineData("(/ 1 0)", "Division by zero")]
    [InlineData("(* 1e308 1e308)", "finite double range")]
    [InlineData("(+ 1 #f)", "Expected a number")]
    [InlineData("(car '())", "Expected a pair")]
    [InlineData("(length '(a . b))", "proper list")]
    [InlineData("((lambda (x) x))", "expects 1 argument")]
    [InlineData("(lambda (x x) x)", "Duplicate parameter")]
    [InlineData("(let ((x 1) (x 2)) x)", "Duplicate binding")]
    public void InvalidOperationsRaiseLanguageRuntimeErrors(string source, string message)
    {
        var error = Assert.Throws<LispRuntimeException>(() => new LispEngine().Execute(source));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void WriterIsInjectedAndOutputProceduresReturnUnspecified()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var engine = new LispEngine(output: output);
        var result = engine.Execute("""
            (display "hello ") (write "world") (newline)
            (print 'value (list #t "text"))
            """);

        Assert.True(result.IsUnspecified);
        Assert.Equal("hello \"world\"\nvalue (#t text)\n", output.ToString());
    }

    [Fact]
    public void ParsedScriptsAreReusableAndGlobalStateIsPerEngine()
    {
        var first = new LispEngine();
        var second = new LispEngine();
        first.SetGlobal("x", LispValue.FromNumber(5));
        var script = first.ParseScript("(set! x (+ x 1)) x", "counter.scm");

        Assert.Equal(6, first.Execute(script).AsNumber());
        Assert.Equal(7, first.Execute(script).AsNumber());
        Assert.Throws<LispRuntimeException>(() => second.Execute(script));
        Assert.True(second.Execute("(define x 30)").IsUnspecified);
        Assert.Equal(31, second.Execute(script).AsNumber());
        Assert.Equal(7, first.Evaluate("x").AsNumber());
    }

    [Fact]
    public void ParseErrorsCarryOneBasedPositionSpanAndFileName()
    {
        var exception = Assert.Throws<LispParseException>(() => new LispEngine().ParseScript("; note\r\n  )", "bad.scm"));

        Assert.Equal(new SourcePosition(10, 2, 3), exception.Position);
        Assert.Equal(new SourcePosition(11, 2, 4), exception.Span.End);
        Assert.Equal("bad.scm", exception.FileName);
        Assert.Contains("Unexpected", exception.Message);
    }

    [Theory]
    [InlineData("(list 1", "Unterminated list")]
    [InlineData("\"abc", "Unterminated string")]
    [InlineData("'", "Expected an expression")]
    [InlineData("'(. a)", "needs an item")]
    [InlineData("'(a . b c)", "after the dotted list tail")]
    [InlineData("9e999", "out-of-range number")]
    public void MalformedSourceRaisesLanguageParseErrors(string source, string message)
    {
        var error = Assert.Throws<LispParseException>(() => new LispEngine().ParseScript(source));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void RuntimeErrorsPointAtTheFailingExpressionInItsDefinitionFile()
    {
        var engine = new LispEngine();
        engine.Execute("(define (raise)\n  missing)", "definition.scm");
        var error = Assert.Throws<LispRuntimeException>(() => engine.Execute("(raise)", "call.scm"));

        Assert.Equal(new SourcePosition(18, 2, 3), error.Position);
        Assert.Equal(new SourcePosition(25, 2, 10), error.Span.End);
        Assert.Equal("definition.scm", error.FileName);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public void ExecutionBudgetResetsAfterBothSuccessfulAndFailedRuns()
    {
        var engine = new LispEngine(new LispOptions { MaximumSteps = 5 });
        Assert.Equal(3, engine.Execute("(+ 1 2)").AsNumber());
        Assert.Equal(3, engine.Execute("(+ 1 2)").AsNumber());
        var error = Assert.Throws<LispRuntimeException>(() => engine.Execute("(begin 1 2 3 4 5)"));
        Assert.Contains("steps", error.Message);
        Assert.Equal(3, engine.Execute("(+ 1 2)").AsNumber());
    }

    [Fact]
    public void TraversingPersistentListDataConsumesExecutionSteps()
    {
        var engine = new LispEngine();
        engine.Execute("(define items '(1 2 3 4 5 6 7 8))");
        engine.Options.MaximumSteps = 5;
        var error = Assert.Throws<LispRuntimeException>(() => engine.Execute("(length items)"));
        Assert.Contains("steps", error.Message);
        engine.Options.MaximumSteps = 20;
        Assert.Equal(8, engine.Execute("(length items)").AsNumber());
    }

    [Theory]
    [InlineData("((((((1))))))")]
    [InlineData("''''''1")]
    public void ReaderLimitsNestedListsAndQuoteAbbreviations(string source)
    {
        var options = new LispOptions { MaximumParseDepth = 6 };
        var engine = new LispEngine(options);
        var error = Assert.Throws<LispParseException>(() => engine.ParseScript(source));
        Assert.Contains("parse depth", error.Message);
        Assert.Equal(7, error.Column);
    }

    [Fact]
    public void EvaluatorLimitsRecursionAndNestedDataIndependentlyOfReader()
    {
        var engine = new LispEngine(new LispOptions { MaximumEvaluationDepth = 8 });
        engine.Execute("(define (loop) (loop))");
        var call = Assert.Throws<LispRuntimeException>(() => engine.Execute("(loop)"));
        Assert.Contains("evaluation depth", call.Message);
        var quoted = Assert.Throws<LispRuntimeException>(() => engine.Execute("'((((((((1))))))))"));
        Assert.Contains("evaluation depth", quoted.Message);
        Assert.Equal(42, engine.Execute("42").AsNumber());
    }

    [Fact]
    public void MaximumSupportedDepthStillRejectsExtremeNestingWithLanguageErrors()
    {
        var engine = new LispEngine(new LispOptions { MaximumParseDepth = 256, MaximumEvaluationDepth = 256 });
        var parseError = Assert.Throws<LispParseException>(() => engine.ParseScript(new string('(', 10_000)));
        Assert.Equal(257, parseError.Column);
        engine.Execute("(define (loop) (loop))");
        var evalError = Assert.Throws<LispRuntimeException>(() => engine.Execute("(loop)"));
        Assert.Contains("evaluation depth", evalError.Message);
    }
}
