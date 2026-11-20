namespace ShoneJS.TypeScript;

public sealed record TypeScriptDiagnostic(
    string Code,
    string Message,
    string? File,
    int Line,
    int Column,
    bool IsError)
{
    public override string ToString() => $"{File ?? "input.ts"}({Line},{Column}): {(IsError ? "error" : "warning")} {Code}: {Message}";
}

public sealed class TypeScriptCheckResult
{
    internal TypeScriptCheckResult(bool succeeded, bool toolAvailable, string? toolVersion,
        IReadOnlyList<TypeScriptDiagnostic> diagnostics, string? failure)
    {
        Succeeded = succeeded;
        ToolAvailable = toolAvailable;
        ToolVersion = toolVersion;
        Diagnostics = failure is null || diagnostics.Any(diagnostic => diagnostic.IsError)
            ? diagnostics
            : [..diagnostics, new TypeScriptDiagnostic("TSADAPTER", failure, null, 1, 1, true)];
        Failure = failure;
    }

    public bool Succeeded { get; }
    public bool ToolAvailable { get; }
    public string? ToolVersion { get; }
    public IReadOnlyList<TypeScriptDiagnostic> Diagnostics { get; }
    public string? Failure { get; }
}
