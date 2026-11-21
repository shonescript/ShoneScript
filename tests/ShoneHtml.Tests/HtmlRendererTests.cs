using System.IO.Pipes;
using System.Text;

namespace ShoneHtml.Tests;

// Exercises the worker and protocol in-process. It does not exercise Windows sandboxing.
public sealed class HtmlRendererTests
{
    private const long Document = 27;
    private const string Origin = "https://renderer.test";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OpensDocumentAndUsesHostReplyForFetchAndHostIntentForNavigation()
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.OpenAsync("""
            <title>Initial</title><a id="next" href="/next?from=worker">Next</a>
            <script type="module">
              const response = await fetch('/message');
              document.title = await response.text();
            </script>
            """);

        var requestFrame = await worker.ReadAsync(HtmlRendererMessage.Request, Document, 1);
        var request = requestFrame.Decode(HtmlRendererJson.Default.HtmlRendererRequest);
        Assert.Equal(HtmlPublicRequestKind.Fetch, request.Kind);
        Assert.Equal(Origin + "/message", request.Address);
        Assert.Equal("GET", request.Method);
        Assert.Null(request.Body);
        await worker.Wire.WriteAsync(HtmlRendererMessage.Resource, Document, requestFrame.Correlation,
            Resource("/message", "From the host", "text/plain"), HtmlRendererJson.Default.HtmlRendererResource, worker.Token);

        var initial = Snapshot(await worker.ReadResultAsync(1));
        Assert.Equal("From the host", initial.Title);
        var mirror = HtmlRendererProjection.Materialize(initial);
        var next = mirror.Document.GetElementById("next");
        Assert.NotNull(next);
        Assert.Equal("#", next.GetAttribute("href"));

        await worker.ClickAsync(2, mirror.Ids[next]);
        var navigation = Assert.IsType<HtmlRendererNavigation>((await worker.ReadResultAsync(2)).Navigation);
        Assert.Equal(Origin + "/next?from=worker", navigation.Address);
        Assert.Equal("GET", navigation.Method);
        Assert.Null(navigation.Body);
    }

    [Fact]
    public async Task ClickAndTimerUpdateMirrorWithoutProjectingScriptsOrEventAttributes()
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.OpenAsync("""
            <title>Interaction</title>
            <button id="run" type="button" onclick="void 0">Run</button>
            <p id="click">Waiting for click</p><p id="timer">Waiting for timer</p>
            <script>
              document.getElementById('run').addEventListener('click', () => {
                document.getElementById('click').textContent = 'Clicked';
                setTimeout(() => { document.getElementById('timer').textContent = 'Timer fired'; }, 1);
              });
            </script>
            """);

        var initial = Snapshot(await worker.ReadResultAsync(1));
        Assert.DoesNotContain(initial.Nodes, node => node.Tag == "script");
        Assert.DoesNotContain(initial.Nodes, node => node.Attributes?.Keys.Any(
            name => name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) == true);
        var mirror = HtmlRendererProjection.Materialize(initial);
        Assert.Null(mirror.Document.QuerySelector("script, [onclick]"));
        Assert.Equal("Waiting for click", mirror.Document.GetElementById("click")!.TextContent);
        Assert.Equal("Waiting for timer", mirror.Document.GetElementById("timer")!.TextContent);
        var button = mirror.Document.GetElementById("run");
        Assert.NotNull(button);

        await worker.ClickAsync(2, mirror.Ids[button]);
        mirror = HtmlRendererProjection.Materialize(Snapshot(await worker.ReadResultAsync(2)));
        Assert.Equal("Clicked", mirror.Document.GetElementById("click")!.TextContent);
        for (var correlation = 3; mirror.Document.GetElementById("timer")!.TextContent != "Timer fired"; correlation++)
        {
            await Task.Delay(10, worker.Token);
            await worker.Wire.WriteAsync(HtmlRendererMessage.Poll, Document, correlation, worker.Token);
            if ((await worker.ReadResultAsync(correlation)).Snapshot is { } snapshot)
                mirror = HtmlRendererProjection.Materialize(snapshot);
        }
    }

    [Fact]
    public async Task LoadsSameOriginScriptAndSkipsScriptWithIntegrity()
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.OpenAsync("""
            <title>Before</title><script src="/app.js"></script>
            <script src="/integrity.js" integrity="sha256-unsupported"></script>
            """);
        var frame = await worker.ReadAsync(HtmlRendererMessage.Request, Document, 1);
        var request = frame.Decode(HtmlRendererJson.Default.HtmlRendererRequest);
        Assert.Equal(HtmlPublicRequestKind.Script, request.Kind);
        Assert.Equal(Origin + "/app.js", request.Address);
        await worker.Wire.WriteAsync(HtmlRendererMessage.Resource, Document, frame.Correlation,
            Resource("/app.js", "document.title = 'External loaded';", "text/javascript"),
            HtmlRendererJson.Default.HtmlRendererResource, worker.Token);
        Assert.Equal("External loaded", Snapshot(await worker.ReadResultAsync(1)).Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CspSuppressesInlineAndExternalScripts(bool header)
    {
        await using var worker = await WorkerPipe.StartAsync();
        var meta = header ? "" : "<meta http-equiv=Content-Security-Policy content=\"script-src 'none'\">";
        var resource = Resource("/index", meta + "<title>Static</title><script>document.title='Ran';</script><script src=/app.js></script>", "text/html");
        if (header) resource.Headers!["Content-Security-Policy"] = "script-src 'none'";
        await worker.Wire.WriteAsync(HtmlRendererMessage.Open, Document, 1, new HtmlRendererOpen(resource, 10_000),
            HtmlRendererJson.Default.HtmlRendererOpen, worker.Token);
        Assert.Equal("Static", Snapshot(await worker.ReadResultAsync(1)).Title);
    }

    [Fact]
    public async Task RejectsMalformedOpenPayload()
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.Wire.WriteAsync(HtmlRendererMessage.Open, Document, 1, worker.Token, "{"u8.ToArray());
        Assert.Equal(1, await worker.Exit.WaitAsync(worker.Token));
    }

    [Theory]
    [InlineData(Document + 1, 2)] // Correct command sequence, wrong document.
    [InlineData(Document, 1)] // Reused command correlation.
    [InlineData(Document, 3)] // Skipped command correlation.
    public async Task RejectsPollForWrongDocumentOrCommandSequence(long document, int correlation)
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.OpenAsync("<title>Ready</title>");
        Assert.Equal("Ready", Snapshot(await worker.ReadResultAsync(1)).Title);

        await worker.Wire.WriteAsync(HtmlRendererMessage.Poll, document, correlation, worker.Token);
        Assert.Equal(1, await worker.Exit.WaitAsync(worker.Token));
    }

    [Fact]
    public async Task RejectsRepeatedWireSequence()
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.OpenAsync("<title>Ready</title>");
        Assert.Equal("Ready", Snapshot(await worker.ReadResultAsync(1)).Title);

        await worker.WritePollWithResetWireSequenceAsync();
        Assert.Equal(1, await worker.Exit.WaitAsync(worker.Token));
    }

    private const string PixelPage = """
        <canvas id="canvas" width="2" height="1"></canvas>
        <img id="webp" src="data:image/webp;base64,UklGRhoAAABXRUJQVlA4TA4AAAAvAAAAEIGI6H8g+h8NDA==">
        <script>
          const ctx = document.getElementById('canvas').getContext('2d');
          ctx.fillStyle = 'rgba(0, 128, 255, 0.5)';
          ctx.fillRect(0, 0, 1, 1);
        </script>
        """;

    [Fact]
    public async Task ProjectsCanvasAndDecodedWebpThroughWorkerProtocol()
    {
        await using var worker = await WorkerPipe.StartAsync();
        await worker.OpenAsync(PixelPage);
        AssertPixelMirror(Snapshot(await worker.ReadResultAsync(1)));
    }

    [PublishedWorkerFact]
    public async Task PublishedWorkerProjectsCanvasAndWebp()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var token = timeout.Token;
        var executable = Path.GetFullPath(Environment.GetEnvironmentVariable(PublishedWorkerFactAttribute.PathVariable)!);
        using var process = await HtmlWindowsSandbox.StartAsync(executable, "--renderer", token);
        using var registration = token.Register(process.Dispose);
        var wire = new HtmlRendererProtocol(process.Input, process.Output);
        Assert.Equal(HtmlRendererMessage.Hello, (await wire.ReadAsync(token)).Type);
        await wire.WriteAsync(HtmlRendererMessage.Open, Document, 1,
            new HtmlRendererOpen(Resource("/index", PixelPage, "text/html"), 10_000), HtmlRendererJson.Default.HtmlRendererOpen, token);
        var frame = await wire.ReadAsync(token);
        Assert.Equal(HtmlRendererMessage.Result, frame.Type);
        var result = frame.Decode(HtmlRendererJson.Default.HtmlRendererResult);
        Assert.Null(result.Error);
        AssertPixelMirror(Snapshot(result));
    }

    [Fact]
    public void RejectsInvalidProjectedPixelShapeAndAggregateBudget()
    {
        HtmlRendererNode root = new(1, 0, "html", null, null, null, null);
        HtmlRendererNode canvas = new(2, 1, "canvas", null, new() { ["width"] = "1", ["height"] = "1" }, null,
            new(1, 1, new byte[4]));
        Assert.Throws<InvalidDataException>(() => HtmlRendererProjection.Materialize(new("", [root,
            canvas with { Pixels = new(1, 1, new byte[3]) }])));
        Assert.Throws<InvalidDataException>(() => HtmlRendererProjection.Materialize(new("", [root,
            canvas with { Pixels = new(2, 1, new byte[8]) }])));
        Assert.Throws<InvalidDataException>(() => HtmlRendererProjection.Materialize(new("", [root,
            canvas with { Tag = "div" }])));
        var large = new HtmlRendererPixels(1024, 1024, new byte[4 * 1024 * 1024]);
        HtmlRendererNode image = new(2, 1, "img", null, null, null, large);
        Assert.Throws<InvalidDataException>(() => HtmlRendererProjection.Materialize(new("",
            [root, image, image with { Id = 3 }, image with { Id = 4 }])));
    }

    private static void AssertPixelMirror(HtmlRendererSnapshot snapshot)
    {
        var mirror = HtmlRendererProjection.Materialize(snapshot);
        var canvas = mirror.Images[mirror.Document.GetElementById("canvas")!];
        Assert.Equal((2, 1), (canvas.Width, canvas.Height));
        Assert.Equal(new byte[] { 0, 128, 255, 128, 0, 0, 0, 0 }, canvas.Rgba);
        var image = mirror.Images[mirror.Document.GetElementById("webp")!];
        Assert.Equal((1, 1), (image.Width, image.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 128 }, image.Rgba);
    }

    private sealed class PublishedWorkerFactAttribute : FactAttribute
    {
        internal const string PathVariable = "SHONEHTML_PUBLISHED_WORKER";

        public PublishedWorkerFactAttribute()
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
                Skip = $"Set {PathVariable} to the Windows worker published for this build.";
        }
    }

    private static HtmlRendererSnapshot Snapshot(HtmlRendererResult result) => Assert.IsType<HtmlRendererSnapshot>(result.Snapshot);

    private static HtmlRendererResource Resource(string path, string text, string contentType) =>
        new(Origin + path, 200, Encoding.UTF8.GetBytes(text), contentType + "; charset=utf-8", [], false);

    private sealed class WorkerPipe : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _host;
        private readonly CancellationTokenSource _timeout;
        internal HtmlRendererProtocol Wire { get; }
        internal CancellationToken Token => _timeout.Token;
        internal Task<int> Exit { get; }

        private WorkerPipe(NamedPipeClientStream host, NamedPipeServerStream worker, CancellationTokenSource timeout)
        {
            _host = host;
            _timeout = timeout;
            Wire = new(host, host);
            Exit = RunAsync(worker);
        }

        internal static async Task<WorkerPipe> StartAsync()
        {
            var name = $"ShoneHtml.Tests.{Guid.NewGuid():N}";
            var worker = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var host = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var timeout = new CancellationTokenSource(Timeout);
            WorkerPipe? fixture = null;
            try
            {
                await Task.WhenAll(worker.WaitForConnectionAsync(timeout.Token), host.ConnectAsync(timeout.Token));
                fixture = new(host, worker, timeout);
                Assert.Empty((await fixture.ReadAsync(HtmlRendererMessage.Hello, 0, 0)).Payload);
                return fixture;
            }
            catch
            {
                if (fixture is not null)
                    await fixture.DisposeAsync();
                else
                {
                    host.Dispose();
                    worker.Dispose();
                    timeout.Dispose();
                }
                throw;
            }
        }

        internal Task OpenAsync(string html) => Wire.WriteAsync(HtmlRendererMessage.Open, Document, 1,
            new HtmlRendererOpen(Resource("/index", html, "text/html"), 10_000), HtmlRendererJson.Default.HtmlRendererOpen, Token);

        internal Task ClickAsync(int correlation, int id) => Wire.WriteAsync(HtmlRendererMessage.Input, Document, correlation,
            new HtmlRendererInput(HtmlRendererInputKind.Click, id), HtmlRendererJson.Default.HtmlRendererInput, Token);

        // A fresh writer starts at sequence 1 even though this connection already sent Open.
        internal Task WritePollWithResetWireSequenceAsync() => new HtmlRendererProtocol(_host, _host)
            .WriteAsync(HtmlRendererMessage.Poll, Document, 2, Token);

        internal async Task<HtmlRendererProtocol.Frame> ReadAsync(HtmlRendererMessage type, long document, int correlation)
        {
            var frame = await Wire.ReadAsync(Token);
            Assert.Equal(type, frame.Type);
            Assert.Equal(document, frame.Document);
            Assert.Equal(correlation, frame.Correlation);
            return frame;
        }

        internal async Task<HtmlRendererResult> ReadResultAsync(int correlation)
        {
            var frame = await ReadAsync(HtmlRendererMessage.Result, Document, correlation);
            var result = frame.Decode(HtmlRendererJson.Default.HtmlRendererResult);
            Assert.Null(result.Error);
            return result;
        }

        private static async Task<int> RunAsync(NamedPipeServerStream pipe)
        {
            using (pipe)
                return await HtmlRendererWorker.RunAsync(pipe, pipe, ["--renderer"]);
        }

        public async ValueTask DisposeAsync()
        {
            _host.Dispose();
            _timeout.Dispose();
            await Exit.WaitAsync(Timeout);
        }
    }
}
