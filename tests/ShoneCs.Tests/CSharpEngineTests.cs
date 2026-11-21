// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

namespace ShoneCs.Tests;

public sealed class CSharpEngineTests
{
    [Fact]
    public async Task EvaluatesFormulaWithDefaultImports()
    {
        var engine = new CSharpEngine();
        var result = await engine.EvaluateFormulaAsync("Enumerable.Range(1, 6).Sum() * 2");

        Assert.Equal(42, Assert.IsType<int>(result));
    }

    [Fact]
    public async Task EvaluatesFormulaWithHostGlobals()
    {
        var engine = new CSharpEngine();
        var result = await engine.EvaluateAsync("Quantity * UnitPrice", new FormulaGlobals(6, 7));

        Assert.Equal(42, Assert.IsType<int>(result));
    }

    [Fact]
    public async Task ExecutesParsedFormulaRepeatedlyWithFreshLocals()
    {
        var engine = new CSharpEngine();
        var formula = engine.ParseFormula("var value = 40; value += 2; value");

        Assert.Equal(42, Assert.IsType<int>(await engine.ExecuteAsync(formula)));
        Assert.Equal(42, Assert.IsType<int>(await engine.ExecuteAsync(formula)));
    }

    [Fact]
    public async Task ExecutesProgramInItsOwnCollectibleLoadContext()
    {
        const string source = """
            using System.Reflection;
            using System.Runtime.Loader;

            var context = AssemblyLoadContext.GetLoadContext(Assembly.GetExecutingAssembly());
            return context?.IsCollectible == true ? 42 : 1;
            """;
        var engine = new CSharpEngine();
        var result = await engine.ExecuteProgramAsync(source);

        Assert.Equal(new SingleFileExecutionResult(42, null), result);
    }

    [Fact]
    public async Task ExecutesSingleFileWithArgumentsAndAsyncExitCode()
    {
        const string source = """
            using System.Threading.Tasks;

            await Task.Yield();
            return int.Parse(args[0]) + int.Parse(args[1]);
            """;
        var directory = Directory.CreateTempSubdirectory("shone-cs-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "Add.cs");
            await File.WriteAllTextAsync(path, source);
            var result = await new CSharpEngine().ExecuteSingleFileAsync(path, ["40", "2"]);

            Assert.Equal(new SingleFileExecutionResult(42, null), result);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}

public sealed record FormulaGlobals(int Quantity, int UnitPrice);
