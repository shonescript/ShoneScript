// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShonePy;

/// <summary>A syntax or runtime error raised while interpreting Python source.</summary>
public sealed class PythonException(string errorName, string message, int line = 0, int column = 0, string? fileName = null)
    : Exception(message)
{
    public string ErrorName { get; } = errorName;
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string? FileName { get; } = fileName;
    internal Runtime.PyExceptionInstance? Instance { get; set; }
    internal bool ContextInitialized { get; set; }

    internal PythonException WithLocation(int line, int column, string? fileName) =>
        new(ErrorName, Message, line, column, fileName) { Instance = Instance, ContextInitialized = ContextInitialized };
}
