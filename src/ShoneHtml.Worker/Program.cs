using ShoneHtml;

if (!OperatingSystem.IsWindows())
    return 1;

try
{
    var exitCode = 1;
    await HtmlWindowsSandbox.RunWorkerAsync(args, async (input, output) =>
    {
        exitCode = await HtmlRendererWorker.RunAsync(input, output, args);
    });
    return exitCode;
}
catch (Exception)
{
    // stdout is exclusively for sandbox IPC.
    return 1;
}
