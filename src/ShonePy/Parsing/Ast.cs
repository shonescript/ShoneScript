// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Parsing;

public abstract record SyntaxNode
{
    public int Line { get; init; }
    public int Column { get; init; }
}

public sealed record ModuleSyntax(List<StatementSyntax> Statements) : SyntaxNode
{
    public string? FileName { get; init; }
}
public abstract record StatementSyntax : SyntaxNode
{
    public IReadOnlyList<ExpressionSyntax> Decorators { get; init; } = [];
}
public sealed record ExpressionStatement(ExpressionSyntax Expression) : StatementSyntax;
public sealed record AssignmentStatement(List<ExpressionSyntax> Targets, ExpressionSyntax Value) : StatementSyntax;
public sealed record AugmentedAssignmentStatement(ExpressionSyntax Target, string Operator, ExpressionSyntax Value) : StatementSyntax;
public sealed record DeleteAttributeStatement(AttributeExpression Target) : StatementSyntax;
public sealed record IfStatement(ExpressionSyntax Test, List<StatementSyntax> Body, List<StatementSyntax> ElseBody) : StatementSyntax;
public sealed record WhileStatement(ExpressionSyntax Test, List<StatementSyntax> Body, List<StatementSyntax> ElseBody) : StatementSyntax;
public sealed record ForStatement(ExpressionSyntax Target, ExpressionSyntax Iterable, List<StatementSyntax> Body, List<StatementSyntax> ElseBody) : StatementSyntax
{
    public bool IsAsync { get; init; }
}
public sealed record FunctionDefinition(string Name, List<string> Parameters, List<StatementSyntax> Body) : StatementSyntax
{
    public IReadOnlyList<ExpressionSyntax> Defaults { get; init; } = [];
    public IReadOnlyDictionary<string, ExpressionSyntax> NamedDefaults { get; init; } = new Dictionary<string, ExpressionSyntax>();
    public int PositionalOnlyCount { get; init; }
    public int KeywordOnlyStart { get; init; }
    public string? VarArgName { get; init; }
    public string? KwArgName { get; init; }
    public bool IsGenerator { get; init; }
    public bool IsAsync { get; init; }
    internal string? FileName { get; init; }
}
public sealed record ClassDefinition(string Name, List<ExpressionSyntax> Bases, List<StatementSyntax> Body) : StatementSyntax;
public sealed record TryStatement(List<StatementSyntax> Body, List<ExceptClause> Handlers,
    List<StatementSyntax> ElseBody, List<StatementSyntax> FinallyBody) : StatementSyntax
{
    internal IEnumerable<List<StatementSyntax>> Suites()
    {
        yield return Body;
        foreach (var handler in Handlers) yield return handler.Body;
        yield return ElseBody;
        yield return FinallyBody;
    }
}
public sealed record ExceptClause(ExpressionSyntax? Type, string? Alias, List<StatementSyntax> Body) : SyntaxNode;
public sealed record WithStatement(List<WithItem> Items, List<StatementSyntax> Body) : StatementSyntax
{
    public bool IsAsync { get; init; }
}
public sealed record WithItem(ExpressionSyntax Context, ExpressionSyntax? Target) : SyntaxNode;
public sealed record RaiseStatement(ExpressionSyntax? Value) : StatementSyntax
{
    public ExpressionSyntax? Cause { get; init; }
}
public sealed record AssertStatement(ExpressionSyntax Test, ExpressionSyntax? Message) : StatementSyntax;
public sealed record ReturnStatement(ExpressionSyntax? Value) : StatementSyntax;
public sealed record BreakStatement : StatementSyntax;
public sealed record ContinueStatement : StatementSyntax;
public sealed record PassStatement : StatementSyntax;
public sealed record ScopeDeclarationStatement(bool IsGlobal, List<string> Names) : StatementSyntax;
public sealed record ImportStatement(List<(string Module, string? Alias)> Imports) : StatementSyntax;
public sealed record FromImportStatement(string Module, List<(string Name, string? Alias)> Imports, int RelativeLevel = 0) : StatementSyntax;

public abstract record ExpressionSyntax : SyntaxNode;
public sealed record NameExpression(string Name) : ExpressionSyntax;
public sealed record ConstantExpression(object? Value) : ExpressionSyntax;
public sealed record YieldExpression(ExpressionSyntax? Value, bool IsDelegating = false) : ExpressionSyntax;
public sealed record AwaitExpression(ExpressionSyntax Operand) : ExpressionSyntax;
public sealed record UnaryExpression(string Operator, ExpressionSyntax Operand) : ExpressionSyntax;
public sealed record BinaryExpression(ExpressionSyntax Left, string Operator, ExpressionSyntax Right) : ExpressionSyntax;
public sealed record ComparisonChainExpression(List<ExpressionSyntax> Operands, List<string> Operators) : ExpressionSyntax;
public enum CallArgumentKind { Positional, Keyword, IterableExpansion, MappingExpansion }
public sealed record CallArgument(CallArgumentKind Kind, ExpressionSyntax Value, string? Name = null);
public sealed record CallExpression(ExpressionSyntax Function, List<ExpressionSyntax> Arguments) : ExpressionSyntax
{
    public IReadOnlyList<(string Name, ExpressionSyntax Value)> Keywords { get; init; } = [];
    public IReadOnlyList<CallArgument> OrderedArguments { get; init; } = [];
}
public sealed record ConditionalExpression(ExpressionSyntax Test, ExpressionSyntax WhenTrue, ExpressionSyntax WhenFalse) : ExpressionSyntax;
public sealed record AttributeExpression(ExpressionSyntax Target, string Name) : ExpressionSyntax;
public sealed record SubscriptExpression(ExpressionSyntax Target, ExpressionSyntax Index) : ExpressionSyntax;
public sealed record SliceExpression(ExpressionSyntax? Start, ExpressionSyntax? Stop, ExpressionSyntax? Step) : ExpressionSyntax;
public sealed record StarredExpression(ExpressionSyntax Value) : ExpressionSyntax;
public sealed record ListExpression(List<ExpressionSyntax> Items) : ExpressionSyntax;
public sealed record SetExpression(List<ExpressionSyntax> Items) : ExpressionSyntax;
public sealed record TupleExpression(List<ExpressionSyntax> Items) : ExpressionSyntax;
public sealed record DictExpression(List<(ExpressionSyntax? Key, ExpressionSyntax Value)> Items) : ExpressionSyntax;
public sealed record ComprehensionClause(ExpressionSyntax Target, ExpressionSyntax Iterable, List<ExpressionSyntax> Filters) : SyntaxNode
{
    public bool IsAsync { get; init; }
}
public sealed record ListComprehensionExpression(ExpressionSyntax Element, List<ComprehensionClause> Clauses) : ExpressionSyntax;
public sealed record SetComprehensionExpression(ExpressionSyntax Element, List<ComprehensionClause> Clauses) : ExpressionSyntax;
public sealed record DictComprehensionExpression(ExpressionSyntax Key, ExpressionSyntax Value, List<ComprehensionClause> Clauses) : ExpressionSyntax;
public sealed record DictUnpackingComprehensionExpression(ExpressionSyntax Mapping, List<ComprehensionClause> Clauses) : ExpressionSyntax;
public sealed record GeneratorExpression(ExpressionSyntax Element, List<ComprehensionClause> Clauses) : ExpressionSyntax;
