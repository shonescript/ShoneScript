// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT
using ShoneJs.Parsing;
using ShoneProlog.Parsing;
using ShoneProlog.Runtime;

namespace ShoneProlog;

public sealed class PrologOptions
{
    public int MaximumParseDepth { get; init; } = 512;
    public int MaximumSearchDepth { get; init; } = 10_000;
    public int MaximumSolutions { get; init; } = 1_000;
}

public sealed record PrologProgram(IReadOnlyList<PrologClause> Clauses, string? FileName = null);
public sealed record PrologQuery(IReadOnlyList<PrologGoal> Goals, IReadOnlyList<string> Variables, string? FileName = null);
public sealed record PrologScript(PrologProgram Program, PrologQuery Query, string? FileName = null);
public sealed record PrologSolution(IReadOnlyDictionary<string, string> Bindings);
public sealed record PrologQueryResult(IReadOnlyList<PrologSolution> Solutions)
{
    public bool Succeeded => Solutions.Count != 0;
}

public sealed class PrologParseException(string message, SourceSpan span, string? fileName = null) : Exception(message)
{
    public SourceSpan Span { get; } = span;
    public SourcePosition Position => Span.Start;
    public int Line => Position.Line;
    public int Column => Position.Column;
    public string? FileName { get; } = fileName;
}

public sealed class PrologRuntimeException(string message) : Exception(message);

public sealed class PrologEngine
{
    private readonly PrologOptions _options;
    private PrologProgram _program = new([]);

    public PrologEngine(PrologOptions? options = null)
    {
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumParseDepth, nameof(PrologOptions.MaximumParseDepth));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumSearchDepth, nameof(PrologOptions.MaximumSearchDepth));
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaximumSolutions, nameof(PrologOptions.MaximumSolutions));
    }

    public PrologProgram ParseProgram(string source, string? fileName = null) => new PrologParser(source, fileName, _options.MaximumParseDepth).ParseProgram();
    public PrologQuery ParseQuery(string source, string? fileName = null) => new PrologParser(source, fileName, _options.MaximumParseDepth).ParseQuery();
    public PrologScript ParseScript(string source, string? fileName = null) => new PrologParser(source, fileName, _options.MaximumParseDepth).ParseScript();

    public void Load(string source, string? fileName = null) => Load(ParseProgram(source, fileName));
    public void Load(PrologProgram program) => _program = program ?? throw new ArgumentNullException(nameof(program));

    public PrologQueryResult Execute(PrologScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        return Run(script.Program.Clauses.Count == 0 ? _program : script.Program, script.Query);
    }

    public PrologQueryResult Execute(PrologQuery query) => Query(query);
    public PrologQueryResult Execute(string source, string? fileName = null) => Execute(ParseScript(source, fileName));
    public PrologQueryResult Evaluate(string source, string? fileName = null) => Execute(source, fileName);

    public PrologQueryResult Query(string source) => Query(ParseQuery(source));
    public PrologQueryResult Query(PrologQuery query) => Run(_program, query);

    private PrologQueryResult Run(PrologProgram program, PrologQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_options.MaximumSolutions == 0) return new([]);
        var predicates = program.Clauses.GroupBy(c => (c.Head.Name, c.Head.Arguments.Count))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var variables = new Dictionary<string, Variable>(StringComparer.Ordinal);
        foreach (var variable in query.Goals.SelectMany(g => g.Variables))
            if (variable.Name != "_") variables.TryAdd(variable.Name, variable);
        var answers = new List<PrologSolution>();
        foreach (Bindings env in Solve(query.Goals, predicates))
        {
            var formatted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in query.Variables)
                if (variables.TryGetValue(name, out var variable)) formatted[name] = TermFormatter.Format(variable, env);
            answers.Add(new(formatted));
            if (answers.Count >= _options.MaximumSolutions) break;
        }
        return new(answers);
    }

    private sealed record PendingGoal(PrologGoal Goal, PendingGoal? Next, int CutBoundary);
    private sealed record ClauseChoice(IReadOnlyList<PrologClause> Clauses, int NextClause, PendingGoal Goal, int Depth, int Mark);
    private sealed class Search
    {
        public Stack<ClauseChoice> Choices { get; } = new();
        public PendingGoal? Goals { get; set; }
        public int Depth { get; set; }
    }
    private sealed record NegationFrame(Search Parent, int Mark);

    private IEnumerable<Bindings> Solve(IReadOnlyList<PrologGoal> query, Dictionary<(string, int), PrologClause[]> predicates)
    {
        // A single binding trail serves all branches; choice points and negation
        // frames restore it to the mark captured before entering that branch.
        var env = new Bindings();
        var frames = new Stack<NegationFrame>();
        var search = new Search { Goals = Prepend(query, null, 0) };
        bool backtrack = false;
        while (true)
        {
            if (backtrack)
            {
                if (search.Choices.TryPop(out var choice))
                {
                    env.Restore(choice.Mark);
                    backtrack = !TryClause(choice);
                    continue;
                }
                if (!frames.TryPop(out var frame)) yield break;
                // Exhausting the inner search makes negation succeed.
                env.Restore(frame.Mark);
                search = frame.Parent;
                backtrack = false;
                continue;
            }

            if (search.Depth > _options.MaximumSearchDepth)
                throw new PrologRuntimeException("Maximum search depth exceeded.");
            if (search.Goals is not { } current)
            {
                if (frames.TryPop(out var frame))
                {
                    // A single inner solution is enough to make negation fail.
                    env.Restore(frame.Mark);
                    search = frame.Parent;
                }
                else yield return env;
                backtrack = true;
                continue;
            }

            search.Goals = current.Next;
            int depth = search.Depth++;
            switch (current.Goal)
            {
                case CutGoal:
                    while (search.Choices.Count > current.CutBoundary) search.Choices.Pop();
                    break;
                case NegationGoal negation:
                    frames.Push(new(search, env.Mark));
                    search = new Search { Goals = new(negation.Goal, null, 0), Depth = search.Depth };
                    break;
                case UnificationGoal unification:
                    backtrack = !Unifier.Unify(unification.Left, unification.Right, env);
                    break;
                case ComparisonGoal comparison:
                    backtrack = !EvaluateComparison(comparison, env);
                    break;
                case CallGoal { Name: "true", Arguments.Count: 0 }:
                    break;
                case CallGoal { Name: "fail", Arguments.Count: 0 }:
                    backtrack = true;
                    break;
                case CallGoal call:
                    backtrack = !predicates.TryGetValue((call.Name, call.Arguments.Count), out var candidates) ||
                        !TryClause(new(candidates, 0, current, depth, env.Mark));
                    break;
                default:
                    throw new PrologRuntimeException("Unsupported goal.");
            }
        }

        bool TryClause(ClauseChoice choice)
        {
            var call = (CallGoal)choice.Goal.Goal;
            for (int i = choice.NextClause; i < choice.Clauses.Count; i++)
            {
                var fresh = choice.Clauses[i].Fresh();
                bool matches = true;
                for (int j = 0; j < call.Arguments.Count; j++)
                    if (!Unifier.Unify(call.Arguments[j], fresh.Head.Arguments[j], env)) { matches = false; break; }
                if (!matches) { env.Restore(choice.Mark); continue; }
                int boundary = search.Choices.Count;
                if (i + 1 < choice.Clauses.Count) search.Choices.Push(choice with { NextClause = i + 1 });
                search.Goals = Prepend(fresh.Body, choice.Goal.Next, boundary);
                search.Depth = choice.Depth + 1;
                return true;
            }
            return false;
        }
    }

    private static PendingGoal? Prepend(IReadOnlyList<PrologGoal> goals, PendingGoal? tail, int cutBoundary)
    {
        for (int i = goals.Count - 1; i >= 0; i--) tail = new(goals[i], tail, cutBoundary);
        return tail;
    }

    private static bool EvaluateComparison(ComparisonGoal comparison, Bindings env)
    {
        if (comparison.Operator == "=") return Unifier.Unify(comparison.Left, comparison.Right, env);
        if (comparison.Operator == "is")
            return Unifier.Unify(comparison.Left, new NumberTerm(Arithmetic.Evaluate(comparison.Right, env)), env);
        double left = Arithmetic.Evaluate(comparison.Left, env), right = Arithmetic.Evaluate(comparison.Right, env);
        return comparison.Operator switch
        {
            "=:=" => left == right, "=\\=" => left != right, "<" => left < right,
            "=<" => left <= right, ">" => left > right, ">=" => left >= right,
            _ => throw new PrologRuntimeException($"Unsupported comparison '{comparison.Operator}'.")
        };
    }
}
