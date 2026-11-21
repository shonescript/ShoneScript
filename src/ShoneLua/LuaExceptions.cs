// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using ShoneJs.Parsing;

namespace ShoneLua;

public sealed class LuaParseException(string message, SourceSpan span, string? fileName = null) : Exception(message)
{
    public SourceSpan Span { get; } = span;
    public SourcePosition Position => Span.Start;
    public string? FileName { get; } = fileName;
}

public sealed class LuaRuntimeException(string message, SourceSpan span, string? fileName = null) : Exception(message)
{
    public SourceSpan Span { get; } = span;
    public SourcePosition Position => Span.Start;
    public string? FileName { get; } = fileName;
}
