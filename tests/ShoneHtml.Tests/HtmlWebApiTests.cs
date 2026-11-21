using System.Collections.Concurrent;
using AngleSharp.Dom;
using static ShoneHtml.Tests.BrowserHttpFixture;

namespace ShoneHtml.Tests;

public sealed class HtmlWebApiTests
{
    [Fact]
    public void TraversalPropertiesAndEventPhasesShareLiveDomState()
    {
        using var page = HtmlPage.Parse("""
            <div id=parent><input id=child type=checkbox></div>
            <script>
              let parent = document.getElementById('parent');
              let child = parent.firstElementChild;
              let selected = document.querySelectorAll('input');
              let children = parent.children;
              child.dataset.itemId = '42';
              child.style.backgroundColor = 'red';
              child.classList.add('ready');
              child.value = 'new';
              parent.append(document.createElement('input'));
              if (selected.length === 1 && children.length === 2 && child === selected[0] &&
                  child.closest('div') === parent && child.value === 'new' && child.dataset.itemId === '42') document.title = '';
              else document.title = 'failed:';
              window.addEventListener('click', e => document.title += e.eventPhase, true);
              parent.addEventListener('click', e => document.title += e.eventPhase, true);
              child.addEventListener('click', e => { document.title += e.eventPhase; e.preventDefault(); }, {once:true});
              parent.addEventListener('click', e => document.title += e.eventPhase);
              window.addEventListener('click', e => document.title += e.eventPhase);
            </script>
            """);
        var child = page.Document.GetElementById("child")!;
        Assert.Equal("ready", child.ClassName);
        Assert.Equal("42", child.GetAttribute("data-item-id"));
        Assert.Equal("background-color: red;", child.GetAttribute("style"));
        Assert.False(page.DispatchEvent(child, "click", cancelable: true));
        Assert.True(page.DispatchEvent(child, "click", cancelable: true));
        Assert.Equal("112331133", page.Title);
    }

    [Fact]
    public async Task ScriptLifecycleWaitsForOrderedDeferredModulesAndIndependentAsyncScripts()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        var releaseAsync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dependencies = 0;
        await using var server = new BrowserHttpFixture(async (request, token) =>
        {
            switch (request.Target)
            {
                case "/": return Response.Text("""
                    <title></title>
                    <script>
                      const note = s => { document.title += s; };
                      note('A'); queueMicrotask(() => note('m'));
                      document.addEventListener('DOMContentLoaded', () => { note('D'); fetch('/release'); });
                      window.addEventListener('load', () => note('L'));
                    </script>
                    <script async src='/async.js'></script>
                    <script src='/classic.js'></script>
                    <script defer src='/defer.js'></script>
                    <script type='module' src='/main.js'></script>
                    <script>note('C');</script>
                    """);
                case "/classic.js": return Js("if (document.readyState === 'loading') note('B');");
                case "/defer.js": return Js("if (document.readyState === 'interactive') note('d');");
                case "/main.js": return Js("""
                    import { letter } from './dep.js';
                    import { letter as again } from './dep.js';
                    const data = await (await fetch('/data')).json();
                    const item = document.createElement('p');
                    item.textContent = data.label;
                    document.body.appendChild(item);
                    note(letter + again);
                    """);
                case "/dep.js": Interlocked.Increment(ref dependencies); return Js("export const letter = 'M';");
                case "/data": return Response.Text("{\"label\":\"Ready\"}", "application/json");
                case "/async.js": await releaseAsync.Task.WaitAsync(token); return Js("note('X');");
                case "/release": releaseAsync.TrySetResult(); return Response.Text("");
                default: return new("text/plain", [], "404 Not Found");
            }
        });
        using var browser = Trusted(errors);
        await browser.NavigateAsync(server.Root).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(errors);
        Assert.Equal("AmBCdMMDXL", browser.Page!.Title);
        Assert.Equal("Ready", browser.Page.Document.QuerySelector("p")?.TextContent);
        Assert.Equal(1, dependencies);
    }

    [Fact]
    public async Task FetchEnforcesCorsAndSupportsRedirectsAbortAndBodyConsumption()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        var reached = new ConcurrentQueue<Request>();
        await using var other = new BrowserHttpFixture(request =>
        {
            reached.Enqueue(request);
            return request.Target == "/allowed"
                ? Response.Text("yes", "text/plain") with { Headers = "Access-Control-Allow-Origin: *\r\nX-Hidden: no\r\nX-Listed: listed\r\nAccess-Control-Expose-Headers: X-Listed\r\n" }
                : Response.Text("secret", "text/plain");
        });
        await using var server = new BrowserHttpFixture(request => request.Target switch
        {
            "/" => Response.Text($$$"""
                <title>waiting</title>
                <script type=module>
                  const foreign = '{{{other.Root.AbsoluteUri}}}';
                  const allowed = await fetch(foreign + 'allowed');
                  const listed = allowed.headers.get('x-listed');
                  const hidden = allowed.headers.get('x-hidden');
                  const text = await allowed.text();
                  let unreadable = false, blocked = false, preflight = false, aborted = false;
                  try { await allowed.text(); } catch(e) { unreadable = e.name === 'TypeError'; }
                  try { await fetch(foreign + 'blocked'); } catch(e) { blocked = e.name === 'TypeError'; }
                  try { await fetch(foreign + 'preflight', {headers:{'x-test':'yes'}}); } catch(e) { preflight = e.name === 'TypeError'; }
                  const controller = new AbortController();
                  controller.abort();
                  try { await fetch('/never', {signal:controller.signal}); } catch(e) { aborted = e.name === 'AbortError'; }
                  const redirected = await fetch('/redirect');
                  const path = new URL(redirected.url).pathname;
                  const url = new URL('/endpoint?x=a+b', location.href);
                  url.searchParams.append('x', '~*');
                  const post = await fetch(new Request(url, {method:'POST', body:url.searchParams}));
                  const reply = await post.text();
                  if (listed === 'listed' && hidden === null && text === 'yes' && unreadable && blocked && preflight && aborted &&
                      redirected.redirected && path === '/endpoint' && reply === 'POST x=a+b&x=%7E*') document.title = 'passed';
                </script>
                """),
            "/redirect" => Response.Redirect("/endpoint"),
            _ when request.Target.StartsWith("/endpoint", StringComparison.Ordinal) => Response.Text(request.Method + " " + request.Body, "text/plain"),
            _ => throw new InvalidOperationException("Unexpected network request: " + request.Target)
        });
        using var browser = Trusted(errors);
        await browser.NavigateAsync(server.Root).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(errors);
        Assert.Equal("passed", browser.Page!.Title);
        Assert.Equal(["/allowed", "/blocked"], reached.Select(x => x.Target));
        Assert.All(reached, x => Assert.Equal(server.Root.GetLeftPart(UriPartial.Authority), x.Headers["Origin"]));
    }

    [Fact]
    public async Task MicrotasksRunBeforeTimersAndNavigationDropsOldPageCallbacks()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new BrowserHttpFixture(async (request, token) =>
        {
            if (request.Target == "/pending")
            {
                requestStarted.TrySetResult();
                await releaseRequest.Task.WaitAsync(token);
                return Response.Text("late");
            }
            return request.Target == "/next" ? Response.Text("<title>next</title>") : Response.Text("""
                <title>start</title>
                <script>
                  const timer = setTimeout(() => document.title = 'unexpected', 1);
                  clearTimeout(timer);
                  Promise.resolve().then(() => { document.title += 'P'; });
                  queueMicrotask(() => { document.title += 'Q'; });
                  setTimeout(letter => { document.title += letter; fetch('/pending').then(() => { document.title = 'late'; }); }, 1, 'T');
                </script>
                """);
        });
        using var browser = Trusted(errors);
        await browser.NavigateAsync(server.Root);
        var previous = browser.Page!;
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await browser.NavigateAsync(server.Address("next"));
        releaseRequest.TrySetResult();
        Assert.True(previous.Lifetime.IsCancellationRequested);
        Assert.Equal("startPQT", previous.Title);
        Assert.Equal("next", browser.Page!.Title);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task TrustedEnvironmentReportsHostMetricsAndOnlyRenderedCssCapabilities()
    {
        string? requestUserAgent = null;
        await using var server = new BrowserHttpFixture(request =>
        {
            requestUserAgent = request.Headers.GetValueOrDefault("User-Agent");
            return Response.Text("""
                <title>pending</title><body><script>
                document.body.dataset.agent = navigator.userAgent;
                document.title = [navigator.platform, screen.width, screen.height, window.devicePixelRatio].join('|');
                document.body.dataset.css = [CSS.supports('display', 'flex'), CSS.supports('(display: grid)'),
                    CSS.supports('selector(div > [hidden]:first-child)'), CSS.supports('color', 'rgb(0 0 0 / 50%)'),
                    CSS.supports('display', 'flow-root'), CSS.supports('selector(:has(a))'), CSS.supports('selector(:focus-visible)'),
                    CSS.supports('width', '1qu'), CSS.supports('position', 'sticky'), CSS.supports('selector(#123)')].join('|');
                </script>
                """);
        });
        var errors = new ConcurrentQueue<HtmlScriptError>();
        using var browser = new HtmlBrowser(new HtmlBrowserOptions
        {
            EnableTrustedScripts = true,
            GetDisplayMetrics = () => new(1440, 960, 1.5),
            ScriptOptions = new() { OnScriptError = errors.Enqueue }
        });
        await browser.NavigateAsync(server.Root).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(errors);
        Assert.StartsWith("ShoneHtml/", requestUserAgent);
        Assert.Equal(requestUserAgent, browser.Page!.Document.Body!.GetAttribute("data-agent"));
        Assert.Equal($"{HtmlBrowser.Platform}|1440|960|1.5", browser.Page.Title);
        Assert.Equal("true|true|true|true|false|false|false|false|false|false",
            browser.Page.Document.Body.GetAttribute("data-css"));
    }

    private static HtmlBrowser Trusted(ConcurrentQueue<HtmlScriptError> errors) => new(new HtmlBrowserOptions
    {
        EnableTrustedScripts = true,
        ScriptOptions = new HtmlPageOptions { OnScriptError = errors.Enqueue }
    });

    private static Response Js(string source) => Response.Text(source, "text/javascript");
}
