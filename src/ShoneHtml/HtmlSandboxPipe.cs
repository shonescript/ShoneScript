using System.IO.Pipes;
using System.Security.Principal;

namespace ShoneHtml;

internal sealed class HtmlSandboxPipe : IDisposable
{
    private NamedPipeServerStream? _host;
    private NamedPipeClientStream? _child;

    internal HtmlSandboxPipe(PipeDirection hostDirection)
    {
        var name = "ShoneHtml." + Guid.NewGuid().ToString("N");
        _host = new NamedPipeServerStream(name, hostDirection, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var childDirection = hostDirection == PipeDirection.In ? PipeDirection.Out : PipeDirection.In;
            _child = new NamedPipeClientStream(".", name, childDirection, PipeOptions.None,
                TokenImpersonationLevel.Anonymous, HandleInheritability.Inheritable);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _child.Connect(2_000);
            _host.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal IntPtr ChildHandle => _child!.SafePipeHandle.DangerousGetHandle();

    internal Stream TakeHostStream()
    {
        _child?.Dispose();
        _child = null;
        return Interlocked.Exchange(ref _host, null) ?? throw new ObjectDisposedException(nameof(HtmlSandboxPipe));
    }

    public void Dispose()
    {
        _child?.Dispose();
        _host?.Dispose();
        _child = null;
        _host = null;
    }
}
