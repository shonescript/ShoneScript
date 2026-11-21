// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using Xunit;

namespace ShoneLua.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("--lisp", "(+ 40 2)", "42")]
    [InlineData("--lua", "return 6 * 7", "42")]
    [InlineData("--lisp", "'()", "()")]
    [InlineData("--lua", "return nil", "nil")]
    public async Task CommandPrintsResult(string mode, string source, string expected)
    {
        var result = await Run(null, mode, "-e", source);
        Assert.Equal((0, expected + Environment.NewLine, ""), result);
    }

    [Theory]
    [InlineData("--lisp", "(define x 40)\n(display (+ x 2))\n(newline)\n")]
    [InlineData("--lua", "local x = 40\nprint(x + 2)\nreturn\n")]
    [InlineData("--lua", "local x = 40\nprint(x + 2)\n")]
    public async Task StandardInputRunsOneMultilineScript(string mode, string source)
    {
        var result = await Run(source, mode);
        Assert.Equal((0, "42" + Environment.NewLine, ""), result);
    }

    [Theory]
    [InlineData("--lisp", "\n)")]
    [InlineData("--lua", "\n)")]
    public async Task SyntaxDiagnosticsGoToStandardError(string mode, string source)
    {
        var result = await Run(source, mode);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.StartsWith("<stdin>:2:1: SyntaxError:", result.Error);
    }

    [Theory]
    [InlineData("--lisp", "(car 1)")]
    [InlineData("--lua", "return missing()")]
    public async Task RuntimeDiagnosticsGoToStandardError(string mode, string source)
    {
        var result = await Run(null, mode, "-e", source);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Matches(@"^<command>:1:\d+: RuntimeError:", result.Error);
    }

    [Theory]
    [InlineData("--lisp")]
    [InlineData("--lua")]
    public async Task MissingScriptArgumentIsAUsageError(string mode)
    {
        var result = await Run(null, mode, "-e");
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Contains("Usage:", result.Error);
    }

    [Fact]
    public async Task HelpListsBothModes()
    {
        var result = await Run(null, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Error);
        Assert.Contains("--lisp [-e <script>]", result.Output);
        Assert.Contains("--lua [-e <script>]", result.Output);
        Assert.Contains("stdin", result.Output);
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string? input, params string[] arguments)
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = testOutput.Parent!.Name;
        var root = testOutput;
        while (!File.Exists(Path.Combine(root.FullName, "ShoneScript.sln")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Could not find ShoneScript.sln.");

        var cli = Path.Combine(root.FullName, "src", "ShoneCli", "bin", configuration, "net11.0", "ShoneScript.dll");
        Assert.True(File.Exists(cli), $"CLI build output not found: {cli}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the CLI.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
