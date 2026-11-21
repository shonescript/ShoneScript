// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using ShoneProlog;

namespace ShoneProlog.Tests;

public sealed class PrologCliTests
{
    [Fact]
    public void EngineReturnsBindingsAndFailure()
    {
        var engine = new PrologEngine();
        engine.Load("parent(alice, bob). parent(alice, carol).");
        Assert.Equal(new[] { "bob", "carol" }, engine.Query("parent(alice, X).").Solutions.Select(s => s.Bindings["X"]));
        Assert.False(engine.Query("parent(nobody, X).").Succeeded);
    }

    [Theory]
    [InlineData("-e", "X = 42.", 0, "X = 42.\n", "")]
    [InlineData("-e", "fail.", 1, "false.\n", "")]
    [InlineData("-e", "?", 1, "", "<command>:1:1: SyntaxError:")]
    public async Task CommandModeReportsAnswersAndDiagnostics(string mode, string query, int code, string output, string error)
    {
        var result = await Run(null, "--prolog", mode, query);
        Assert.Equal(code, result.ExitCode);
        Assert.Equal(output.Replace("\n", Environment.NewLine), result.Output);
        Assert.StartsWith(error, result.Error);
    }

    [Fact]
    public async Task StdinPreservesMarkerContextAndDiagnosticCoordinates()
    {
        var valid = await Run("p('?-'). % ?- is not a separator\n?- p(X).", "--prolog");
        Assert.Equal((0, "X = '?-'." + Environment.NewLine, ""), valid);

        var invalid = await Run("p(a).\r\n?- p(@).", "--prolog");
        Assert.Equal(1, invalid.ExitCode);
        Assert.Equal("", invalid.Output);
        Assert.StartsWith("<stdin>:2:6: SyntaxError:", invalid.Error);
    }

    [Fact]
    public async Task StdinAndFileCanContainProgramAndQuery()
    {
        const string input = "parent(alice, bob).\n?- parent(alice, X).\n";
        var stdin = await Run(input, "--prolog");
        Assert.Equal((0, "X = bob." + Environment.NewLine, ""), stdin);

        string file = Path.Combine(Path.GetTempPath(), $"prolog-{Guid.NewGuid():N}.pl");
        try
        {
            await File.WriteAllTextAsync(file, input);
            var script = await Run(null, "--prolog", file);
            Assert.Equal((0, "X = bob." + Environment.NewLine, ""), script);
        }
        finally { File.Delete(file); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> Run(string? input, params string[] arguments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "ShoneScript.sln")))
            root = root.Parent ?? throw new DirectoryNotFoundException("Could not find ShoneScript.sln.");
        var cli = Path.Combine(root.FullName, "src", "ShoneCli", "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net11.0", "ShoneScript.dll");
        Assert.True(File.Exists(cli), $"CLI build output not found: {cli}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
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
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
