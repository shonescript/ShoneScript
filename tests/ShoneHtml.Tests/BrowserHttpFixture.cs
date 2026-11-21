using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ShoneHtml.Tests;

internal sealed class BrowserHttpFixture : IAsyncDisposable
{
    internal sealed record Request(string Method, string Target, string Body, IReadOnlyDictionary<string, string> Headers);
    internal sealed record Response(string Type, byte[] Body, string Status = "200 OK", string Headers = "")
    {
        public static Response Text(string body, string type = "text/html; charset=utf-8") => new(type, Encoding.UTF8.GetBytes(body));
        public static Response Redirect(string location) => new("text/plain", [], "302 Found", $"Location: {location}\r\n");
    }

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private readonly List<Task> _connections = [];

    public BrowserHttpFixture(Func<Request, CancellationToken, Task<Response>> handler)
    {
        _listener.Start();
        Root = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
        _accepting = AcceptAsync(handler);
    }

    public BrowserHttpFixture(Func<Request, Response> handler) : this((request, _) => Task.FromResult(handler(request))) { }

    public Uri Root { get; }
    public Uri Address(string target) => new(Root, target);

    private async Task AcceptAsync(Func<Request, CancellationToken, Task<Response>> handler)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(RespondAsync(client, handler, _stop.Token));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private static async Task RespondAsync(TcpClient client, Func<Request, CancellationToken, Task<Response>> handler, CancellationToken token)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var start = (await reader.ReadLineAsync(token))?.Split(' ', 3);
                if (start is not [var method, var target, _])
                    return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon]] = line[(colon + 1)..].Trim();
                }
                var length = int.TryParse(headers.GetValueOrDefault("Content-Length"), out var size) ? size : 0;
                var body = new char[length];
                if (length > 0)
                    await reader.ReadBlockAsync(body, token);
                var response = await handler(new Request(method, target, new string(body), headers), token);
                var head = $"HTTP/1.1 {response.Status}\r\nConnection: close\r\nContent-Type: {response.Type}\r\nContent-Length: {response.Body.Length}\r\n{response.Headers}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
                await stream.WriteAsync(response.Body, token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _accepting;
        _listener.Stop();
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
