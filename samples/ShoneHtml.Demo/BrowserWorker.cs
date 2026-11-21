using System.Diagnostics;
using System.Reflection;
using ShoneJs;

// This probes termination and a fixed protocol only. A same-user process is not a sandbox.
internal static class BrowserWorker
{
    public static bool TryRun(string[] args)
    {
        if (args is ["--browser-worker-probe"])
        {
            if (Console.OpenStandardInput().ReadByte() == 1)
            {
                using var output = Console.OpenStandardOutput();
                output.WriteByte(1);
                output.Flush();
                new Engine(new EngineOptions { MaximumLoopIterations = int.MaxValue, MaximumExecutionSteps = int.MaxValue })
                    .Evaluate("while (true) {}");
            }
            return true;
        }
        if (args is not ["--verify-browser-worker"])
            return false;
        VerifyAsync().GetAwaiter().GetResult();
        return true;
    }

    private static async Task VerifyAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--browser-worker-probe");
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot start worker probe.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            child.StandardInput.BaseStream.WriteByte(1);
            child.StandardInput.BaseStream.Flush();
            var reply = new byte[1];
            await child.StandardOutput.BaseStream.ReadExactlyAsync(reply, deadline.Token);
            if (reply[0] != 1 || child.HasExited)
                throw new InvalidOperationException("Worker probe handshake failed.");
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(deadline.Token);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }
}
