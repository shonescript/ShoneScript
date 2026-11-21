using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ShoneHtml;

// Uses a host-created empty file and a private loopback listener. The normal renderer does not have this command.
internal static class HtmlWindowsSandboxProbe
{
    internal const string Mode = "--verify-renderer";

    internal static async Task VerifyAsync(string executable, CancellationToken token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ShoneHtml.Probe.{Guid.NewGuid():N}");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete | FileShare.Inheritable, 1, FileOptions.DeleteOnClose);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var child = await HtmlWindowsSandbox.StartAsync(executable, Mode, token).ConfigureAwait(false);
        var name = Encoding.UTF8.GetBytes(path);
        if (name.Length > 4096) throw new InvalidOperationException("Probe file path exceeds the expected length.");
        var header = new byte[16];
        BitConverter.TryWriteBytes(header, port);
        BitConverter.TryWriteBytes(header.AsSpan(4), file.SafeFileHandle.DangerousGetHandle().ToInt64());
        BitConverter.TryWriteBytes(header.AsSpan(12), name.Length);
        await child.Output.WriteAsync(header, token).ConfigureAwait(false);
        await child.Output.WriteAsync(name, token).ConfigureAwait(false);
        await child.Output.FlushAsync(token).ConfigureAwait(false);
        var result = new byte[1];
        await child.Input.ReadExactlyAsync(result, token).ConfigureAwait(false);
        if (result[0] != 15 || listener.Pending())
            throw new InvalidOperationException($"LPAC denial probe failed (file read, write, TCP and extra handle mask {result[0]}/15).");
        child.Kill();
        if (await child.WaitForExitAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("LPAC probe process did not report forced termination.");
    }

    internal static async Task RunAsync(Stream input, Stream output)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var token = deadline.Token;
        var header = new byte[16];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var port = BitConverter.ToInt32(header);
        var pointer = new IntPtr(BitConverter.ToInt64(header, 4));
        var length = BitConverter.ToInt32(header, 12);
        if (port is < 1 or > 65535 || pointer == IntPtr.Zero || length is < 1 or > 4096)
            throw new InvalidDataException("Invalid LPAC probe input.");
        var name = new byte[length];
        await input.ReadExactlyAsync(name, token).ConfigureAwait(false);
        var path = Encoding.UTF8.GetString(name);
        if (!Path.IsPathFullyQualified(path) || !Path.GetFileName(path).StartsWith("ShoneHtml.Probe.", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid LPAC probe file name.");

        byte result = 0;
        if (Denied(path, FileAccess.Read)) result |= 1;
        if (Denied(path, FileAccess.Write)) result |= 2;
        using (var client = new TcpClient(AddressFamily.InterNetwork))
        {
            try { await client.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied) { result |= 4; }
        }
        using (var inherited = new SafeFileHandle(pointer, ownsHandle: false))
        {
            var buffer = new char[4096];
            const uint fileNameOpened = 8;
            var count = GetFinalPathNameByHandleW(inherited, buffer, buffer.Length, fileNameOpened);
            if (count < buffer.Length && (count == 0 ||
                !new string(buffer, 0, (int)count).EndsWith(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                result |= 8;
        }
        await output.WriteAsync(new byte[] { result }, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
    }

    private static bool Denied(string path, FileAccess access)
    {
        try
        {
            using var sentinel = File.Open(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, int length, uint flags);
}
