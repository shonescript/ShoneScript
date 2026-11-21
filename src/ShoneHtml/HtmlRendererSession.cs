using System.Threading.Channels;
using System.Text.Json;

namespace ShoneHtml;

internal sealed class HtmlRendererSession : IDisposable
{
    // Release gate: only enable for a deployment that passed the Windows isolation and publish checks.
    internal static bool IsReleased => false;
    internal static string WorkerExecutable => Path.Combine(AppContext.BaseDirectory, "ShoneHtml.Worker", "ShoneHtml.Worker.exe");
    private readonly Channel<HtmlRendererInput> _inputs = Channel.CreateBounded<HtmlRendererInput>(64);
    private readonly CancellationTokenSource _stop;
    private readonly CancellationToken _token;
    private readonly HtmlWindowsSandboxProcess _process;
    private readonly HtmlRendererProtocol _wire;
    private readonly HtmlPublicNetwork.Scope _scope;
    private readonly Uri _origin;
    private readonly long _document;
    private int _request, _command = 1, _disposed;
    private long _displayBytes;

    private HtmlRendererSession(HtmlWindowsSandboxProcess process, HtmlPublicNetwork.Scope scope, Uri origin, long document, CancellationToken lifetime)
    {
        _process = process;
        _wire = new(process.Input, process.Output);
        _scope = scope;
        _origin = origin;
        _document = document;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _token = _stop.Token;
    }

    internal static async Task<(HtmlRendererSession Session, HtmlRendererResult Initial)> OpenAsync(HtmlPublicNetwork.Scope scope,
        HtmlResource resource, long document, HtmlBrowserOptions options, CancellationToken operation, CancellationToken lifetime)
    {
        if (!HtmlWindowsSandbox.IsSupported || !File.Exists(WorkerExecutable)) throw new IOException("Isolated renderer unavailable.");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(operation, lifetime);
        startup.CancelAfter(TimeSpan.FromSeconds(5));
        var process = await HtmlWindowsSandbox.StartAsync(WorkerExecutable, "--renderer", startup.Token).ConfigureAwait(false);
        var session = new HtmlRendererSession(process, scope, resource.Address, document, lifetime);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(operation, session._token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            using (deadline.Token.Register(session.Dispose))
            {
                var hello = await session._wire.ReadAsync(deadline.Token).ConfigureAwait(false);
                if (hello.Type != HtmlRendererMessage.Hello || hello.Document != 0 || hello.Correlation != 0)
                    throw new InvalidDataException("Renderer handshake failed.");
            }
            deadline.Token.ThrowIfCancellationRequested();
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using (deadline.Token.Register(session.Dispose))
            {
                var config = new HtmlRendererOpen(HtmlRendererResource.From(resource), options.ScriptOptions.MaximumLoopIterations,
                    options.MaximumDocumentNodes, options.MaximumDocumentDepth);
                await session._wire.WriteAsync(HtmlRendererMessage.Open, document, 1, config, HtmlRendererJson.Default.HtmlRendererOpen, deadline.Token).ConfigureAwait(false);
                var first = await session.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (first.Snapshot is null) throw new InvalidDataException("Missing renderer display data.");
                return (session, first);
            }
        }
        catch { session.Dispose(); throw; }
    }

    internal void Send(HtmlRendererInput input)
    {
        if (input.Text?.Length > 32768 || !_inputs.Writer.TryWrite(input)) Dispose();
    }

    internal async Task RunAsync(Func<HtmlRendererResult, Task> apply, Func<Task> failed)
    {
        try
        {
            while (!_token.IsCancellationRequested)
            {
                HtmlRendererInput? input = null;
                if (!_inputs.Reader.TryRead(out input))
                {
                    await Task.Delay(100, _token).ConfigureAwait(false);
                    _inputs.Reader.TryRead(out input);
                }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                HtmlRendererResult result;
                using (deadline.Token.Register(Dispose))
                {
                    var command = checked(++_command);
                    if (input is null)
                        await _wire.WriteAsync(HtmlRendererMessage.Poll, _document, command, deadline.Token).ConfigureAwait(false);
                    else
                        await _wire.WriteAsync(HtmlRendererMessage.Input, _document, command, input, HtmlRendererJson.Default.HtmlRendererInput, deadline.Token).ConfigureAwait(false);
                    result = await ReceiveAsync(deadline.Token).ConfigureAwait(false);
                }
                if (!_token.IsCancellationRequested) await apply(result).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsRendererFailure(ex)) { }
        finally { Dispose(); await failed().ConfigureAwait(false); }
    }

    internal static bool IsRendererFailure(Exception ex) => ex is IOException or InvalidDataException or JsonException or HttpRequestException or
        InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException;

    private async Task<HtmlRendererResult> ReceiveAsync(CancellationToken token)
    {
        while (true)
        {
            var frame = await _wire.ReadAsync(token).ConfigureAwait(false);
            if (frame.Document != _document) throw new InvalidDataException("Wrong renderer document.");
            if (frame.Type == HtmlRendererMessage.Result && frame.Correlation == _command)
            {
                if ((_displayBytes += frame.Payload.Length) > 128 * 1024 * 1024)
                    throw new InvalidDataException("Renderer display traffic budget exceeded.");
                var result = frame.Decode(HtmlRendererJson.Default.HtmlRendererResult);
                if (result.FocusedId < 0 || result.Error?.Length > 512 || result.Navigation is { } nav && !ValidNavigation(nav))
                    throw new InvalidDataException("Invalid renderer result.");
                return result;
            }
            if (frame.Type != HtmlRendererMessage.Request || frame.Correlation != checked(++_request))
                throw new InvalidDataException("Unexpected renderer message.");
            var request = frame.Decode(HtmlRendererJson.Default.HtmlRendererRequest);
            var resource = await LoadResourceAsync(request, token).ConfigureAwait(false);
            await _wire.WriteAsync(HtmlRendererMessage.Resource, _document, frame.Correlation, resource,
                HtmlRendererJson.Default.HtmlRendererResource, token).ConfigureAwait(false);
        }
    }

    private async Task<HtmlRendererResource> LoadResourceAsync(HtmlRendererRequest request, CancellationToken token)
    {
        var denied = new HtmlRendererResource(null, 0, null, null, null, false);
        if (request.Kind is not (HtmlPublicRequestKind.Style or HtmlPublicRequestKind.Image or HtmlPublicRequestKind.Script or HtmlPublicRequestKind.Fetch) ||
            request.Address is not { Length: > 0 and <= 8192 } || !Uri.TryCreate(request.Address, UriKind.Absolute, out var url) ||
            !HtmlResources.IsHttpAddress(url) || request.Method is not ("GET" or "HEAD" or "POST" or "PUT" or "DELETE" or "PATCH" or "OPTIONS") ||
            request.Body?.Length > 256 * 1024 || request.Headers?.Count > 32)
            throw new InvalidDataException("Invalid renderer resource request.");
        if (request.Kind != HtmlPublicRequestKind.Fetch && (request.Method != "GET" || request.Body is not null ||
            request.Headers is not null || request.Mode is not null || request.Redirect is not null))
            throw new InvalidDataException("Invalid renderer subresource request.");
        if (request.Kind == HtmlPublicRequestKind.Fetch &&
            (request.Mode is not ("same-origin" or "cors" or "no-cors") || request.Redirect is not ("follow" or "error")))
            throw new InvalidDataException("Invalid renderer fetch policy.");

        var cross = !HtmlResources.SameOrigin(_origin, url);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.Headers is { } supplied)
            foreach (var (name, value) in supplied)
            {
                if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase) || value is null || !headers.TryAdd(name, value))
                    throw new InvalidDataException("Invalid renderer fetch header.");
            }
        string? origin = null;
        if (cross && request.Kind == HtmlPublicRequestKind.Script) return denied;
        if (request.Kind == HtmlPublicRequestKind.Fetch && (cross || request.Mode == "no-cors"))
        {
            if (request.Mode == "same-origin" || !SimpleRequest(request.Method, headers)) return denied;
            if (cross) headers["Origin"] = origin = _origin.GetLeftPart(UriPartial.Authority);
        }
        bool AllowRedirect(Uri _, Uri next) => request.Kind switch
        {
            HtmlPublicRequestKind.Script => HtmlResources.SameOrigin(_origin, next),
            HtmlPublicRequestKind.Fetch => !cross && request.Redirect == "follow" && HtmlResources.SameOrigin(_origin, next),
            _ => true
        };
        try
        {
            var response = await _scope.LoadAsync(url, token, request.Kind, new HttpMethod(request.Method), request.Body,
                headers: headers, allowRedirect: AllowRedirect).ConfigureAwait(false);
            var media = response.ContentType?.Split(';', 2)[0].Trim();
            if (response.Bytes.Length > HtmlRendererProtocol.MaximumResourceBytes ||
                request.Kind == HtmlPublicRequestKind.Style && !string.Equals(media, "text/css", StringComparison.OrdinalIgnoreCase) ||
                request.Kind == HtmlPublicRequestKind.Image && media?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                request.Kind == HtmlPublicRequestKind.Script && !HtmlScriptHost.IsJavaScriptMediaType(media)) return denied;
            if (origin is not null)
            {
                if (request.Mode == "no-cors") return new(response.Address.AbsoluteUri, 200, [], null, [], false);
                if (!response.Headers.TryGetValue("Access-Control-Allow-Origin", out var allowed) || allowed != "*" && allowed != origin) return denied;
            }
            var resource = HtmlRendererResource.From(response);
            var exposed = origin is not null && resource.Headers!.TryGetValue("Access-Control-Expose-Headers", out var names)
                ? names.Split(',').Select(name => name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var key in resource.Headers!.Keys.ToArray())
            {
                var name = key.ToLowerInvariant();
                if (name is "set-cookie" or "set-cookie2" || origin is not null && name is not ("access-control-allow-origin" or
                    "access-control-expose-headers" or "cache-control" or "content-language" or "content-length" or "content-type" or
                    "expires" or "last-modified" or "pragma") && exposed?.Contains(name) != true && exposed?.Contains("*") != true)
                    resource.Headers.Remove(key);
            }
            return resource;
        }
        catch (HttpRequestException) when (!_scope.IsExhausted) { return denied; }
    }

    private static bool SimpleRequest(string method, Dictionary<string, string> headers) => method is "GET" or "HEAD" or "POST" &&
        headers.All(pair => pair.Value.Length <= 128 && pair.Key.ToLowerInvariant() switch
        {
            "accept" => !pair.Value.Any(Unsafe),
            "accept-language" or "content-language" => pair.Value.All(c => char.IsAsciiLetterOrDigit(c) || " *,-.;=".Contains(c)),
            "content-type" => !pair.Value.Any(Unsafe) && pair.Value.Split(';', 2)[0].Trim().ToLowerInvariant() is
                "application/x-www-form-urlencoded" or "multipart/form-data" or "text/plain",
            _ => false
        });

    private static bool Unsafe(char c) => c > 126 || c < ' ' && c != '\t' || "\"():<>?@[\\]{}".Contains(c);

    private static bool ValidNavigation(HtmlRendererNavigation nav) => nav.Address is { Length: > 0 and <= 8192 } &&
        Uri.TryCreate(nav.Address, UriKind.Absolute, out var address) && HtmlResources.IsHttpAddress(address) &&
        (nav.Method == "GET" && nav.Body is null && (nav.Query is null || nav.Query.Length <= 8192) ||
         nav.Method == "POST" && nav.Body is { Length: <= 256 * 1024 } && nav.Query is null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inputs.Writer.TryComplete();
        _stop.Cancel();
        _process.Dispose();
        _stop.Dispose();
    }
}
