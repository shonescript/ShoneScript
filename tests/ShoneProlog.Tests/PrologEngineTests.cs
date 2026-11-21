// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneProlog.Tests;

public sealed class PrologEngineTests
{
    [Fact]
    public void ScriptSeparatesTheProgramFromItsOneFinalQuery()
    {
        var engine = new PrologEngine();
        engine.Load("p(loaded).");
        var script = engine.ParseScript("p('?-'). % ?- in a comment\n/* ?- */ ?- p(X).", "source.pl");
        Assert.Single(script.Program.Clauses);
        Assert.Equal("source.pl", script.Query.FileName);
        Assert.Equal("'?-'", engine.Execute(script).Solutions.Single().Bindings["X"]);
        Assert.Equal("loaded", engine.Evaluate("?- p(X)").Solutions.Single().Bindings["X"]);
        Assert.Equal("\"?-\"", engine.Execute("X = \"?-\". % ?-").Solutions.Single().Bindings["X"]);
        Assert.Throws<PrologParseException>(() => engine.ParseProgram("p(a). ?- p(X)."));
        Assert.Throws<PrologParseException>(() => engine.ParseQuery("p(a). ?- p(X)."));
        Assert.Throws<PrologParseException>(() => engine.ParseScript("p(a). ?- p(X). ?- p(Y)."));
    }

    [Theory]
    [InlineData("p(ok).\r\n?- p(X, @).", 16, 2, 9)]
    [InlineData("p(ok).\n?- p(X, ).", 15, 2, 9)]
    [InlineData("p(ok).\n?- X = 'oops", 14, 2, 8)]
    [InlineData("p(ok).\n?- X =", 13, 2, 7)]
    public void DiagnosticsKeepAbsolutePositions(string source, int offset, int line, int column)
    {
        var error = Assert.Throws<PrologParseException>(() => new PrologEngine().ParseScript(source, "input.pl"));
        Assert.Equal("input.pl", error.FileName);
        Assert.Equal(new SourcePosition(offset, line, column), error.Position);
        Assert.True(error.Span.End.Offset >= error.Span.Start.Offset);
    }

    [Fact]
    public void AnonymousVariablesAreIndependentAndClauseVariablesAreFresh()
    {
        var engine = new PrologEngine();
        engine.Load("p(a, b). wildcard(_, _). same(X, X). one(X) :- p(X, _). one(X) :- p(_, X).");
        Assert.Empty(engine.Query("p(_, _), wildcard(a, b).").Solutions.Single().Bindings);
        Assert.Equal("a", engine.Query("p(_Named, _).").Solutions.Single().Bindings["_Named"]);
        Assert.False(engine.Query("p(_Named, _Named).").Succeeded);
        Assert.False(engine.Query("same(a, b).").Succeeded);
        Assert.Equal(4, engine.Query("one(X), one(Y).").Solutions.Count);
        Assert.True(engine.Query("same(a, a), same(b, b).").Succeeded);
    }

    [Fact]
    public void StringsAndQuotedAtomsAreDistinctAndRoundTrip()
    {
        var engine = new PrologEngine();
        var result = engine.Evaluate("'mixed case'('it''s fine', \"a\\nb\"). ?- 'mixed case'(A, B).");
        Assert.Equal("'it\\'s fine'", result.Solutions.Single().Bindings["A"]);
        Assert.Equal("\"a\\nb\"", result.Solutions.Single().Bindings["B"]);
        Assert.False(engine.Query("'word' = \"word\".").Succeeded);
        Assert.True(engine.Query("'word' = word.").Succeeded);
    }

    [Theory]
    [InlineData("[a | T] = [a, b, c].", "T", "[b, c]")]
    [InlineData("[a | T] = [a].", "T", "[]")]
    [InlineData("T = [a | R], R = [b | [c]].", "T", "[a, b, c]")]
    [InlineData("T = f(X, [X | R]), X = a, R = b.", "T", "f(a, [a | b])")]
    [InlineData("[a, b] = [a | T], T = [X].", "X", "b")]
    public void UnifiesAndFormatsListsAndNestedBindings(string query, string name, string expected) =>
        Assert.Equal(expected, new PrologEngine().Query(query).Solutions.Single().Bindings[name]);

    [Theory]
    [InlineData("[] = [a].")]
    [InlineData("[a] = [a, b].")]
    [InlineData("[a | b] = [a, b].")]
    [InlineData("X = [X].")]
    public void IncompatibleOrCyclicListsFailWithoutLooping(string query) =>
        Assert.False(new PrologEngine().Query(query).Succeeded);

    [Theory]
    [InlineData("2 + 3 * 4", "14")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("10 - 3 - 2", "5")]
    [InlineData("24 / 4 / 2", "3")]
    [InlineData("-2 + 3 * -(1 + 2)", "-11")]
    [InlineData("1e2 + 0.25", "100.25")]
    public void ArithmeticBindsAnUnboundLeftSide(string expression, string expected) =>
        Assert.Equal(expected, new PrologEngine().Query($"X is {expression}.").Solutions.Single().Bindings["X"]);

    [Fact]
    public void BacktrackingRestoresBindingsAndNegationDoesNotLeak()
    {
        var engine = new PrologEngine();
        engine.Load("p(a). p(b). q(Y) :- p(Y), Y = b. q(c).");
        Assert.Equal(new[] { "b", "c" }, engine.Query("q(X).").Solutions.Select(s => s.Bindings["X"]));
        Assert.False(engine.Query("\\+ p(X), X = b.").Succeeded);
        Assert.Equal("z", engine.Query("\\+ q(z), X = z.").Solutions.Single().Bindings["X"]);
        Assert.False(engine.Query("X = 3, X is 4.").Succeeded);
        Assert.Throws<PrologRuntimeException>(() => engine.Query("X is 1 / 0."));
    }

    [Fact]
    public void CutPrunesOnlyAlternativesInItsPredicateInvocation()
    {
        var engine = new PrologEngine();
        engine.Load("p(a). p(b). choose(X) :- p(X), !. choose(c).");
        var solutions = engine.Query("p(Outer), choose(Inner).").Solutions;
        Assert.Equal(new[] { "a/a", "b/a" }, solutions.Select(s => $"{s.Bindings["Outer"]}/{s.Bindings["Inner"]}"));
    }

    [Fact]
    public void DepthAndSolutionLimitsAreEnforced()
    {
        var engine = new PrologEngine(new() { MaximumParseDepth = 2 });
        Assert.Throws<PrologParseException>(() => engine.ParseQuery("X = f(f(f(a)))."));
        Assert.Throws<PrologParseException>(() => engine.ParseQuery("\\+ \\+ \\+ true."));
        Assert.Throws<PrologParseException>(() => engine.ParseQuery("X is 1 + 2 + 3 + 4."));

        var recursive = new PrologEngine();
        recursive.Load("loop(X) :- loop(X). negative_loop :- \\+ negative_loop.");
        Assert.Contains("depth", Assert.Throws<PrologRuntimeException>(() => recursive.Query("loop(a).")).Message);
        Assert.Contains("depth", Assert.Throws<PrologRuntimeException>(() => recursive.Query("negative_loop.")).Message);

        var limited = new PrologEngine(new() { MaximumSolutions = 1 });
        limited.Load("first(a). first(X) :- first(X).");
        Assert.Equal("a", limited.Query("first(X).").Solutions.Single().Bindings["X"]);
        Assert.Empty(new PrologEngine(new() { MaximumSolutions = 0 }).Query("true.").Solutions);
    }
}
