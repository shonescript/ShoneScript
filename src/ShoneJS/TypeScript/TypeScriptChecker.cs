using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ShoneJS.TypeScript;

/// <summary>Runs the official TypeScript native checker for one source snapshot.</summary>
public sealed class TypeScriptChecker
{
    private static readonly Regex VersionRegex = new(@"(?<!\d)(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?![\w.-])", RegexOptions.Compiled);
    private static readonly Regex DiagnosticRegex = new(
        @"^(?<file>.*?)(?:\((?<line>\d+),(?<column>\d+)\)|:(?<line2>\d+):(?<column2>\d+))\s*[-:]\s*(?<kind>error|warning)\s*(?<code>TS\d+)?\s*:?[ \t]*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Checks the original source with the configured TypeScript checker in no-emit mode.</summary>
    public TypeScriptCheckResult Check(string source, TypeScriptOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new TypeScriptOptions();
        if (string.IsNullOrWhiteSpace(options.RequiredVersion))
            return Failure("RequiredVersion must be specified.");
        if (options.Timeout <= TimeSpan.Zero || options.Timeout.TotalMilliseconds > int.MaxValue)
            return Failure("Timeout must be positive and no greater than Int32.MaxValue milliseconds.");

        var root = Path.Combine(Path.GetTempPath(), "shonejs-ts-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, "input.ts");
        try
        {
            Directory.CreateDirectory(root);
            // Keep the exact source snapshot used by the interpreter so diagnostic positions match.
            File.WriteAllText(file, source, new UTF8Encoding(false));
            var tool = ResolveTool(options.ToolPath);
            if (tool is null) return Failure("The TypeScript native checker was not found.");

            var version = Run(tool, ["--version"], options, root);
            if (!version.Started || version.ExitCode != 0)
                return Failure(version.Failure ?? (version.TimedOut
                    ? "The TypeScript native checker version probe timed out."
                    : "The TypeScript native checker version probe failed."));
            var actualVersion = VersionRegex.Match(version.Stdout + "\n" + version.Stderr);
            if (!actualVersion.Success || !string.Equals(actualVersion.Groups[1].Value, options.RequiredVersion, StringComparison.Ordinal))
                return new TypeScriptCheckResult(false, true, actualVersion.Success ? actualVersion.Groups[1].Value : null,
                    [], $"TypeScript native checker version mismatch (required {options.RequiredVersion}).");

            // The checker is deliberately restricted to noEmit and one staged source file.
            var check = Run(tool, ["--noEmit", "--pretty", "false", "--strict", "--target", "es2023", "--allowJs", "false", file], options, root);
            var diagnostics = ParseDiagnostics(check.Stdout + "\n" + check.Stderr, file);
            if (!check.Started)
                return new TypeScriptCheckResult(false, true, actualVersion.Groups[1].Value, diagnostics, check.Failure);
            if (check.TimedOut)
                return new TypeScriptCheckResult(false, true, actualVersion.Groups[1].Value, diagnostics, "TypeScript checker timed out.");
            if (check.ExitCode != 0 || diagnostics.Any(static diagnostic => diagnostic.IsError))
                return new TypeScriptCheckResult(false, true, actualVersion.Groups[1].Value, diagnostics,
                    diagnostics.Count == 0 ? "TypeScript checker failed without a parseable diagnostic." : null);
            return new TypeScriptCheckResult(true, true, actualVersion.Groups[1].Value, diagnostics, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure($"TypeScript checker failed: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* cleanup is best effort */ }
        }
    }

    private static string? ResolveTool(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        return null;
    }

    private static RunResult Run(string tool, IReadOnlyList<string> args, TypeScriptOptions options, string cwd)
    {
        var psi = new ProcessStartInfo { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        if (tool.EndsWith(".js", StringComparison.OrdinalIgnoreCase)) { psi.FileName = "node"; psi.ArgumentList.Add(tool); }
        else psi.FileName = tool;
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return new(false, false, -1, "Process could not be started.", "", "");
            using var deadline = new CancellationTokenSource(options.Timeout);
            // Drain both pipes concurrently so diagnostic output cannot block the checker.
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try
            {
                Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr).GetAwaiter().GetResult();
                return new(true, false, process.ExitCode, null, stdout.Result, stderr.Result);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return new(true, true, -1, null, "", "");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(false, false, -1, ex.Message, "", ""); }
    }

    private static List<TypeScriptDiagnostic> ParseDiagnostics(string output, string stagedFile)
    {
        var result = new List<TypeScriptDiagnostic>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DiagnosticRegex.Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            var lineText = match.Groups["line"].Success ? match.Groups["line"].Value : match.Groups["line2"].Value;
            var columnText = match.Groups["column"].Success ? match.Groups["column"].Value : match.Groups["column2"].Value;
            if (!int.TryParse(lineText, out var lineNumber) || !int.TryParse(columnText, out var column)) continue;
            var file = match.Groups["file"].Value;
            if (Path.GetFullPath(file).Equals(Path.GetFullPath(stagedFile), StringComparison.OrdinalIgnoreCase)) file = "input.ts";
            result.Add(new(match.Groups["code"].Value, match.Groups["message"].Value, file, lineNumber, column,
                match.Groups["kind"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)));
        }
        return result;
    }

    private static TypeScriptCheckResult Failure(string message) => new(false, false, null,
        [new TypeScriptDiagnostic("TSADAPTER", message, null, 1, 1, true)], message);
    private readonly record struct RunResult(bool Started, bool TimedOut, int ExitCode, string? Failure, string Stdout, string Stderr);
}
