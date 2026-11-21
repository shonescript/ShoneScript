using ShoneHtml;

namespace ShoneHtml.Tests;

public sealed class HtmlWindowsSandboxTests
{
    [Fact]
    public async Task WorkerRejectsArgumentsBeforeInvokingRenderer()
    {
        var invoked = false;

        await Assert.ThrowsAsync<ArgumentException>(() => HtmlWindowsSandbox.RunWorkerAsync([], (_, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }));

        Assert.False(invoked);
    }

    [Fact]
    public async Task WorkerRejectsUnknownModeBeforeInvokingRenderer()
    {
        var invoked = false;

        await Assert.ThrowsAsync<ArgumentException>(() => HtmlWindowsSandbox.RunWorkerAsync(["--trusted"], (_, _) =>
        {
            invoked = true;
            return Task.CompletedTask;
        }));

        Assert.False(invoked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31000)]
    public async Task VerifyRejectsUnboundedTimeout(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            HtmlWindowsSandbox.VerifyAsync(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void LauncherDoesNotClaimUnsupportedPlatforms()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            Assert.Throws<PlatformNotSupportedException>(() => HtmlWindowsSandbox.Start("worker.exe", "--renderer"));
        else
            Assert.Throws<FileNotFoundException>(() => HtmlWindowsSandbox.Start(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), "--renderer"));
    }
}
