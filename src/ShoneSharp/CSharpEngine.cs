using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace ShoneCs;

/// <summary>Options shared by formula evaluation and single-file compilation.</summary>
public sealed class ShoneCsOptions
{
    public IEnumerable<string> Imports { get; init; } = ["System", "System.Math", "System.Linq", "System.Collections.Generic"];
    public IEnumerable<Assembly> References { get; init; } = [typeof(object).Assembly, typeof(Enumerable).Assembly];
    public bool AllowUnsafe { get; init; }
}

/// <summary>A parsed formula. The compilation is associated with the default load context.</summary>
public sealed class ParsedFormula
{
    internal ParsedFormula(Script<object?> script) => Script = script;
    internal Script<object?> Script { get; }
}

/// <summary>Result returned by a single-file program.</summary>
public sealed record SingleFileExecutionResult(int ExitCode, object? ReturnValue);

/// <summary>Compiles C# formulas and isolated single-file programs.</summary>
public sealed class CSharpEngine
{
    private readonly ShoneCsOptions _options;

    public CSharpEngine(ShoneCsOptions? options = null) => _options = options ?? new ShoneCsOptions();

    /// <summary>Parses a formula without executing it. Formula scripts use the default AssemblyLoadContext.</summary>
    public ParsedFormula ParseFormula(string formula, string? fileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formula);
        var scriptOptions = CreateScriptOptions();
        return new ParsedFormula(CSharpScript.Create<object?>(formula, scriptOptions, globalsType: null));
    }

    public Task<object?> EvaluateFormulaAsync(string formula, CancellationToken cancellationToken = default) =>
        EvaluateFormulaAsync(formula, globals: null, cancellationToken);

    public Task<object?> EvaluateFormulaAsync(string formula, object? globals,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formula);
        return CSharpScript.EvaluateAsync<object?>(formula, CreateScriptOptions(), globals,
            cancellationToken: cancellationToken);
    }

    // Convenient alias matching the other Shone language engines.
    public Task<object?> EvaluateAsync(string formula, object? globals = null,
        CancellationToken cancellationToken = default) =>
        EvaluateFormulaAsync(formula, globals, cancellationToken);

    public Task<object?> ExecuteAsync(ParsedFormula formula, object? globals = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formula);
        return formula.Script.RunAsync(globals, cancellationToken: cancellationToken)
            .ContinueWith(static task => task.GetAwaiter().GetResult().ReturnValue,
                cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Executes a C# source file in a new collectible AssemblyLoadContext.</summary>
    public async Task<SingleFileExecutionResult> ExecuteSingleFileAsync(string filePath,
        string[]? arguments = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var source = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        return await ExecuteProgramAsync(source, filePath, arguments, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Compiles and executes one C# program in a new collectible AssemblyLoadContext.</summary>
    public async Task<SingleFileExecutionResult> ExecuteProgramAsync(string source,
        string? fileName = null, string[]? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        cancellationToken.ThrowIfCancellationRequested();

        var syntaxTree = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Latest), cancellationToken: cancellationToken);
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(fileName) is { Length: > 0 } name ? name : "ShoneCsProgram",
            [syntaxTree], GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                allowUnsafe: _options.AllowUnsafe, optimizationLevel: OptimizationLevel.Release));

        await using var image = new MemoryStream();
        var emit = compilation.Emit(image, cancellationToken: cancellationToken);
        if (!emit.Success)
            throw new SharpCompilationException(emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        image.Position = 0;
        var loadContext = new ScriptLoadContext();
        try
        {
            var assembly = loadContext.LoadFromStream(image);
            var entryPoint = assembly.EntryPoint
                ?? throw new SharpCompilationException("The source does not contain an entry point.");
            object?[]? invocationArguments = entryPoint.GetParameters().Length == 0
                ? null
                : [arguments ?? []];
            var value = entryPoint.Invoke(null, invocationArguments);
            var (exitCode, returnValue) = await UnwrapResultAsync(value).ConfigureAwait(false);
            return new SingleFileExecutionResult(exitCode, returnValue);
        }
        finally
        {
            loadContext.Unload();
        }
    }

    private ScriptOptions CreateScriptOptions() => ScriptOptions.Default
        .WithImports(_options.Imports)
        .WithReferences(_options.References);

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var paths = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("The runtime does not expose trusted platform assemblies.");
        return paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
    }

    private static async Task<(int ExitCode, object? ReturnValue)> UnwrapResultAsync(object? value)
    {
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")?.GetValue(task);
            return result is int code ? (code, null) : (0, result);
        }
        if (value is ValueTask valueTask)
        {
            await valueTask.ConfigureAwait(false);
            return (0, null);
        }
        return value is int exitCode ? (exitCode, null) : (0, value);
    }

    private sealed class ScriptLoadContext() : AssemblyLoadContext($"ShoneCs-{Guid.NewGuid():N}", isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}

public sealed class SharpCompilationException : Exception
{
    public SharpCompilationException(string message) : base(message) { }
    public SharpCompilationException(IEnumerable<Diagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => d.ToString()))) => Diagnostics = diagnostics.ToArray();
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = [];
}
