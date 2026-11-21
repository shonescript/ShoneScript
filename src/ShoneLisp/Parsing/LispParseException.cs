// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneLisp.Parsing;

public sealed class LispParseException(string message, SourceSpan span, string? fileName = null) : Exception(message)
{
    public SourceSpan Span { get; } = span;
    public SourcePosition Position => Span.Start;
    public int Line => Position.Line;
    public int Column => Position.Column;
    public string? FileName { get; } = fileName;
}
