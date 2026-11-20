using ShoneJS.TypeScript;

namespace ShoneJS.Tests;

public sealed class TypeScriptCheckerTests
{
    [Fact]
    public void MissingToolFailsClosedWithAdapterDiagnostic()
    {
        var result = new TypeScriptChecker().Check("let value: number = 1;", new TypeScriptOptions
        {
            ToolPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-tsgo.exe")
        });

        Assert.False(result.Succeeded);
        Assert.False(result.ToolAvailable);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TSADAPTER", diagnostic.Code);
        Assert.True(diagnostic.IsError);
    }

    [Fact]
    public void InvalidTimeoutFailsClosed()
    {
        var result = new TypeScriptChecker().Check("let value = 1;", new TypeScriptOptions
        {
            Timeout = TimeSpan.Zero
        });

        Assert.False(result.Succeeded);
        Assert.Equal("TSADAPTER", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void DiagnosticFormattingPreservesPositionAndCode()
    {
        var diagnostic = new TypeScriptDiagnostic("TS2322", "Type string is not assignable to type number.", "input.ts", 1, 5, true);

        Assert.Equal("input.ts(1,5): error TS2322: Type string is not assignable to type number.", diagnostic.ToString());
        var exception = new TypeScriptCheckException([diagnostic]);
        Assert.Same(diagnostic, Assert.Single(exception.Diagnostics));
        Assert.Contains("input.ts(1,5)", exception.Message);
    }
}
