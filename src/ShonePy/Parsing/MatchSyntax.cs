// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy.Parsing;

public sealed record MatchStatement(ExpressionSyntax Subject, List<MatchCase> Cases) : StatementSyntax;
public sealed record MatchCase(PatternSyntax Pattern, ExpressionSyntax? Guard, List<StatementSyntax> Body,
    IReadOnlySet<string> Captures) : SyntaxNode;

public abstract record PatternSyntax : SyntaxNode;
public sealed record CapturePattern(string? Name) : PatternSyntax; // A null name is the wildcard.
public sealed record ValuePattern(ExpressionSyntax Value, bool IsSingleton) : PatternSyntax;
public sealed record SequencePattern(List<PatternSyntax> Items) : PatternSyntax;
public sealed record StarPattern(string? Name) : PatternSyntax;
public sealed record OrPattern(List<PatternSyntax> Alternatives) : PatternSyntax;
public sealed record AsPattern(PatternSyntax Pattern, string Name) : PatternSyntax;
public sealed record MappingPattern(List<(ExpressionSyntax Key, PatternSyntax Pattern)> Items, string? Rest) : PatternSyntax;
public sealed record ClassPattern(ExpressionSyntax Class, List<PatternSyntax> Positional,
    List<(string Name, PatternSyntax Pattern)> Keywords) : PatternSyntax;
