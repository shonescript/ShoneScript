namespace ShoneJS.TypeScript;

/// <summary>Raised when TypeScript diagnostics prevent execution.</summary>
public sealed class TypeScriptCheckException : Exception
{
    public TypeScriptCheckException(IReadOnlyList<TypeScriptDiagnostic> diagnostics)
        : base(Format(diagnostics))
    {
        Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public IReadOnlyList<TypeScriptDiagnostic> Diagnostics { get; }

    private static string Format(IReadOnlyList<TypeScriptDiagnostic>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0) return "TypeScript checking failed.";
        return string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.ToString()));
    }
}
