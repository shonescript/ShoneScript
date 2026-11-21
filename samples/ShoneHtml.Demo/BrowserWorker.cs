using System.ComponentModel;
using ShoneHtml;

internal static class BrowserWorker
{
    internal static int Verify()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Browser worker verification requires Windows.");
            return 1;
        }

        try
        {
            var result = HtmlWindowsSandbox.VerifyAsync().GetAwaiter().GetResult();
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Browser worker verification failed: {Describe(error)}");
            for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
                Console.Error.WriteLine($"  {Describe(inner)}");
            return 1;
        }
    }

    private static string Describe(Exception error) => error is Win32Exception native
        ? $"{error.Message} (Win32 error {native.NativeErrorCode})"
        : error.Message;
}
