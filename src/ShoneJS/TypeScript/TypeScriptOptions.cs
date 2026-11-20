namespace ShoneJS.TypeScript;

/// <summary>Configuration for the official TypeScript native checker.</summary>
public sealed class TypeScriptOptions
{
    /// <summary>Path to the native checker executable or its JavaScript entry point.</summary>
    public string? ToolPath { get; set; }

    /// <summary>The exact native compiler version accepted by this adapter.</summary>
    public string RequiredVersion { get; set; } = "7.0.2";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

}
