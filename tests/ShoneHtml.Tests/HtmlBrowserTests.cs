using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using static ShoneHtml.Tests.BrowserHttpFixture;

namespace ShoneHtml.Tests;

public sealed class HtmlBrowserTests
{
    // Loopback fixtures are app-owned and use the explicitly trusted transport with scripts off.
    private static readonly HtmlBrowserOptions StaticFixture = new()
    {
        ScriptMode = HtmlBrowserScriptMode.TrustedInProcess, ScriptOptions = new() { EnableScripts = false }
    };

    [Theory]
    [InlineData(HtmlBrowserScriptMode.Auto)]
    [InlineData(HtmlBrowserScriptMode.Disabled)]
    public async Task PublicModesRefuseLoopbackBeforeSendingRequests(HtmlBrowserScriptMode mode)
    {
        var requests = 0;
        await using var server = new BrowserHttpFixture(_ =>
        {
            Interlocked.Increment(ref requests);
            return Response.Text("<script>document.title='unexpected';</script>");
        });
        using var browser = new HtmlBrowser(new HtmlBrowserOptions { ScriptMode = mode, EnableTrustedScripts = false });
        await browser.NavigateAsync(server.Root);
        Assert.Null(browser.Page);
        Assert.False(browser.IsLoading);
        Assert.Equal(0, requests);
    }
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task RedirectEncodingBaseStylesAndRasterUseResolvedAddresses()
    {
        using var browser = new HtmlBrowser(StaticFixture);
        await using var server = new BrowserHttpFixture(request => request.Target switch
        {
            "/start" => Response.Redirect("/docs/index"),
            "/docs/index" => new("text/html", Encoding.Latin1.GetBytes("""
                <meta charset=windows-1252><title>Café</title><base href='../assets/'>
                <link rel=stylesheet href='theme.css'><img src='pixel.png'><img src='missing.png' alt='Missing'>
                <a href='../next'>Next</a><script>document.title = 'Remote script ran';</script>
                """)),
            "/assets/theme.css" => Response.Text("body { color: red; }", "text/css"),
            "/assets/pixel.png" => new("image/png", Pixel),
            _ => new("text/plain", [], "404 Not Found")
        });

        await browser.NavigateAsync(server.Address("start#anchor"));
        var page = Assert.IsType<HtmlPage>(browser.Page);
        Assert.Equal("Caf\u00e9", page.Title);
        Assert.Equal(server.Address("docs/index#anchor"), browser.Address);
        Assert.Equal("anchor", browser.ScrollFragment);
        Assert.Equal(server.Address("assets/"), page.BaseAddress);
        Assert.Equal(server.Address("next"), page.ResolveAddress("../next"));
        Assert.Equal("body { color: red; }", page.GetExternalStyle(page.Document.QuerySelector("link")!));
        Assert.Equal(Pixel, page.GetImage(page.Document.QuerySelector("img")!));
        Assert.Contains("unavailable", browser.Status);
    }

    [Fact]
    public async Task DirectImageNavigationUsesTheResponseWithoutRefetching()
    {
        var requests = 0;
        await using var server = new BrowserHttpFixture(request =>
        {
            Interlocked.Increment(ref requests);
            return request.Target == "/pixel"
                ? new Response("image/png", Pixel)
                : new Response("image/svg+xml", "<svg/>"u8.ToArray());
        });
        using var browser = new HtmlBrowser(StaticFixture);
        await browser.NavigateAsync(server.Address("pixel"));
        var page = Assert.IsType<HtmlPage>(browser.Page);
        var image = Assert.Single(page.Document.QuerySelectorAll("img"));
        Assert.Equal(Pixel, page.GetImage(image));
        Assert.Null(page.Document.QuerySelector("pre"));
        Assert.Equal(1, requests);

        await browser.NavigateAsync(server.Address("unsupported"));
        Assert.Null(browser.Page!.Document.QuerySelector("img, pre"));
        Assert.Contains("Unsupported or invalid image", browser.Status);
    }

    [Fact]
    public async Task LoadsEmbeddedBase64RasterWithoutAllowingDataNavigation()
    {
        var requests = 0;
        var source = "data:image/png;base64," + Convert.ToBase64String(Pixel);
        await using var server = new BrowserHttpFixture(_ =>
        {
            Interlocked.Increment(ref requests);
            return Response.Text($"<img id=valid src='{source}'><img id=invalid src='data:image/png;base64,@@@@'>");
        });
        using var browser = new HtmlBrowser(StaticFixture);
        await browser.NavigateAsync(server.Root);
        var page = Assert.IsType<HtmlPage>(browser.Page);
        Assert.Equal(Pixel, page.GetImage(page.Document.GetElementById("valid")!));
        Assert.Null(page.GetImage(page.Document.GetElementById("invalid")!));
        Assert.Equal(1, requests);
        await browser.NavigateAsync(new Uri(source));
        Assert.Same(page, browser.Page);
    }

    [Fact]
    public async Task FragmentHistoryAndReloadRestoreScrollAndTruncateForwardEntries()
    {
        var requests = 0;
        await using var server = new BrowserHttpFixture(request =>
        {
            Interlocked.Increment(ref requests);
            return Response.Text($"<title>{request.Target}</title><a href='#here'>Anchor</a><p id=here>Here</p>");
        });
        using var browser = new HtmlBrowser(StaticFixture);
        await browser.NavigateAsync(server.Address("one"));
        var first = browser.Page!;
        browser.UpdateScroll(123.5);
        await first.ActivateAsync(first.Document.QuerySelector("a")!);
        Assert.Same(first, browser.Page);
        Assert.Equal(1, requests);
        Assert.Equal("here", browser.ScrollFragment);
        browser.UpdateScroll(275);
        await browser.GoBackAsync();
        Assert.Equal(123.5, browser.ScrollOffset);
        Assert.Equal("", browser.Address!.Fragment);
        await browser.GoForwardAsync();
        Assert.Equal(275, browser.ScrollOffset);
        await browser.RefreshAsync();
        Assert.Equal(2, requests);
        Assert.NotSame(first, browser.Page);
        Assert.Equal(275, browser.ScrollOffset);
        await browser.GoBackAsync();
        await browser.NavigateAsync(server.Address("other"));
        Assert.False(browser.CanGoForward);
        await browser.GoBackAsync();
        Assert.Equal(server.Address("one"), browser.Address);
        Assert.Equal(123.5, browser.ScrollOffset);
    }

    [Fact]
    public async Task SubmitsSuccessfulControlsInTreeOrderAndEncodesGetAndPost()
    {
        await using var server = new BrowserHttpFixture(request => request.Target == "/form"
            ? Response.Text("""
                <form id=cart action='submit?discard=1'>
                  <input name=q value='a b*~'>
                  <label for=on>On</label><input id=on name=enabled type=checkbox>
                  <input name=bad disabled value=x><fieldset disabled><input name=bad value=y></fieldset>
                  <input id=r1 name=choice type=radio checked value=one><input id=r2 name=choice type=radio value=two>
                  <select name=item multiple><option selected value=1>One<option selected value=2>Two</select>
                  <textarea name=notes>a
                b</textarea>
                  <button id=get name=go value=search>Find</button>
                  <button id=post formmethod=post name=go value=save>Save</button>
                </form><input form=cart name=outside value='&#x4E8C;'>
                """)
            : Response.Text("<title>submitted</title><pre>" + System.Net.WebUtility.HtmlEncode(
                $"{request.Method} {request.Target}\n{request.Headers.GetValueOrDefault("Content-Type")}\n{request.Body}") + "</pre>"));
        using var browser = new HtmlBrowser(StaticFixture);
        await browser.NavigateAsync(server.Address("form"));
        var page = browser.Page!;
        await page.ActivateAsync(page.Document.QuerySelector("label")!);
        await page.ActivateAsync(page.Document.GetElementById("r2")!);
        Assert.False(Assert.IsAssignableFrom<IHtmlInputElement>(page.Document.GetElementById("r1")).IsChecked);
        await page.ActivateAsync(page.Document.GetElementById("get")!);
        Assert.StartsWith("GET /submit?q=a+b*%7E&enabled=on&choice=two&item=1&item=2&notes=a%0D%0Ab&go=search&outside=%E4%BA%8C\n",
            browser.Page!.Document.QuerySelector("pre")!.TextContent);

        await browser.GoBackAsync();
        page = browser.Page!;
        await page.ActivateAsync(page.Document.GetElementById("post")!);
        Assert.Equal("?discard=1", browser.Address!.Query);
        Assert.Equal("POST /submit?discard=1\napplication/x-www-form-urlencoded\n" +
            "q=a+b*%7E&choice=one&item=1&item=2&notes=a%0D%0Ab&go=save&outside=%E4%BA%8C",
            browser.Page!.Document.QuerySelector("pre")!.TextContent);
    }

    [Fact]
    public async Task ModuleApplicationLoadsAndUpdatesListThroughFormInteraction()
    {
        var errors = new System.Collections.Concurrent.ConcurrentQueue<HtmlScriptError>();
        await using var server = new BrowserHttpFixture(request => (request.Method, request.Target) switch
        {
            ("GET", "/") => Response.Text("""
                <title>Inventory</title><ul id=items></ul>
                <form method=post action=/unexpected><input name=item><button>Add</button></form>
                <script type=module src=/app.js></script>
                """),
            ("GET", "/app.js") => Response.Text("""
                import { items, add } from './items.js';
                const list = document.querySelector('#items');
                const input = document.querySelector('input');
                const render = () => {
                  list.textContent = '';
                  items.forEach(name => {
                    const row = document.createElement('li');
                    row.textContent = name;
                    list.appendChild(row);
                  });
                };
                render();
                document.querySelector('form').addEventListener('submit', event => {
                  event.preventDefault();
                  add(input.value);
                  input.value = '';
                  render();
                });
                """, "text/javascript"),
            ("GET", "/items.js") => Response.Text("""
                export let items = await (await fetch('/items')).json();
                export const add = name => { items = items.concat([name]); };
                """, "text/javascript"),
            ("GET", "/items") => Response.Text("[\"Alpha\",\"Beta\"]", "application/json"),
            _ => new("text/plain", [], "404 Not Found")
        });
        using var browser = new HtmlBrowser(new HtmlBrowserOptions
        {
            EnableTrustedScripts = true,
            ScriptOptions = new() { OnScriptError = errors.Enqueue }
        });
        await browser.NavigateAsync(server.Root).WaitAsync(TimeSpan.FromSeconds(8));
        await browser.RunAsync(async () =>
        {
            var page = Assert.IsType<HtmlPage>(browser.Page);
            string[] Rows() => page.Document.QuerySelectorAll("#items li").Select(e => e.TextContent).ToArray();
            Assert.Empty(errors);
            Assert.Equal(["Alpha", "Beta"], Rows());
            var field = Assert.IsAssignableFrom<IHtmlInputElement>(page.Document.QuerySelector("input"));
            field.Value = "Gamma";
            page.FormControlChanged(field);
            await page.ActivateAsync(page.Document.QuerySelector("button")!);
            Assert.Empty(errors);
            Assert.Same(page, browser.Page);
            Assert.Equal(["Alpha", "Beta", "Gamma"], Rows());
            Assert.Equal("", field.Value);
        });
    }

    [Fact]
    public async Task SupersededAndStoppedRequestsCannotPublish()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new BrowserHttpFixture(async (request, token) =>
        {
            if (request.Target == "/slow")
            {
                began.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return Response.Text($"<title>{request.Target}</title>");
        });
        using var browser = new HtmlBrowser(StaticFixture);
        var slow = browser.NavigateAsync(server.Address("slow"));
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await browser.NavigateAsync(server.Address("fast"));
        await slow.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal("/fast", browser.Page!.Title);
        Assert.False(browser.IsLoading);
        Assert.False(browser.CanGoBack);
        browser.Stop();
        Assert.True(browser.Page.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task RejectsOversizeAndUnsupportedRedirectAndShowsHttpStatus()
    {
        await using var server = new BrowserHttpFixture(request => request.Target switch
        {
            "/huge" => new("text/html", new byte[1024]),
            "/redirect" => Response.Redirect("file:///test.html"),
            _ => new("text/html", "<title>Not found</title>"u8.ToArray(), "404 Not Found")
        });
        using var browser = new HtmlBrowser(new HtmlBrowserOptions { MaximumResponseBytes = 128, ScriptMode = StaticFixture.ScriptMode, ScriptOptions = StaticFixture.ScriptOptions });
        await browser.NavigateAsync(server.Address("huge"));
        Assert.Null(browser.Page);
        Assert.Contains("limit", browser.Status);
        await browser.NavigateAsync(server.Address("redirect"));
        Assert.Null(browser.Page);
        Assert.Contains("unsupported", browser.Status);
        await browser.NavigateAsync(server.Address("missing"));
        Assert.Equal("Not found", browser.Page!.Title);
        Assert.Contains("404", browser.Status);
    }

    [Fact]
    public async Task RejectsDeepDocumentsWithoutCommittingAndDisposeCancelsPendingNavigation()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new BrowserHttpFixture(async (request, token) =>
        {
            if (request.Target == "/deep")
                return Response.Text(string.Concat(Enumerable.Repeat("<div>", 64)));
            began.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response.Text("");
        });
        using var browser = new HtmlBrowser(new HtmlBrowserOptions { MaximumDocumentDepth = 12, ScriptMode = StaticFixture.ScriptMode, ScriptOptions = StaticFixture.ScriptOptions });
        await browser.NavigateAsync(server.Address("deep"));
        Assert.Null(browser.Page);
        Assert.Contains("depth", browser.Status);
        var pending = browser.NavigateAsync(server.Address("pending"));
        await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
        browser.Dispose();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(browser.Page);
    }
}
