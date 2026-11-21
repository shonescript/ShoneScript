using System.Collections.Concurrent;
using AngleSharp.Dom;
using static ShoneHtml.Tests.BrowserHttpFixture;

namespace ShoneHtml.Tests;

public sealed class HtmlWebApiTests
{
    [Fact]
    public void BrowserDomConstructorsSemanticsAndAbortCombinatorsAreAvailable()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <form id=f><input id=i list=choices required pattern="[a-z]+"><datalist id=choices><option value=x></datalist></form>
            <template id=t><b>kept inert</b></template><dialog id=d></dialog><script>
              const input = document.getElementById('i');
              const form = document.getElementById('f');
              const choices = document.getElementById('choices');
              const template = document.getElementById('t');
              const dialog = document.getElementById('d');
              if (!(input instanceof HTMLInputElement) || !(dialog instanceof HTMLDialogElement) ||
                  !(document.createElement('div') instanceof HTMLElement)) throw new Error('HTML brands');
              if (input.validity.valueMissing !== true || input.checkValidity() !== false || form.reportValidity() !== false) throw new Error('validation');
              if (input.list !== choices) throw new Error('input.list');
              if (choices.options.length !== 1) throw new Error('datalist.options ' + choices.options.length);
              if (!(template.content instanceof DocumentFragment)) throw new Error('template fragment');
              if (template.content.querySelector('b').textContent !== 'kept inert') throw new Error('template content');
              if (URL.canParse('/x', 'https://example.test/') !== true || URL.parse('%%%') !== null) throw new Error('URL static methods');
              const source = AbortSignal.abort('stop');
              document.title = 'STATIC ABORT';
              const controller = new AbortController();
              document.title = 'CONTROLLER';
              const joined = AbortSignal.any([controller.signal, source]);
              if (!joined.aborted || joined.reason !== 'stop' || !('timeout' in AbortSignal)) throw new Error('abort signals');
              document.title = 'ABORT';
              const parser = new DOMParser();
              const parsed = parser.parseFromString('<strong>parsed</strong>', 'text/html');
              document.title = 'PARSED';
              const serializer = new XMLSerializer();
              const serialized = serializer.serializeToString(parsed.querySelector('strong'));
              if (parsed.querySelector('strong').textContent !== 'parsed' || serialized.indexOf('<strong>') < 0) throw new Error('serialization');
              document.title = 'PASS';
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.True(errors.Count == 0, $"{page.Title}: {string.Join(Environment.NewLine, errors.Select(error => error.Exception.Message))}");
        Assert.Equal("PASS", page.Title);
    }

    [Fact]
    public void ReplaceWithMovesNodesAndInsertsLiteralTextInArgumentOrder()
    {
        var errors = new List<HtmlScriptError>();
        using var page = HtmlPage.Parse("""
            <main><i id=old></i><b id=next></b><u id=end></u></main><strong id=move>move</strong>
            <script>
              const old = document.getElementById('old');
              if (!(old instanceof Element) || typeof Element.prototype.replaceWith !== 'function') throw new Error('Element prototype');
              const next = document.getElementById('next');
              const moved = document.getElementById('move');
              const fragment = document.createDocumentFragment();
              fragment.appendChild(document.createTextNode('fragment'));
              old.replaceWith('<x>', next, fragment, moved, old, moved);
              if (old.parentElement.localName !== 'main' || fragment.firstChild !== null ||
                  moved !== document.getElementById('move')) throw new Error('node state');
              try { old.replaceWith(moved, old.parentElement); } catch (error) {
                if (error.name !== 'TypeError') throw error;
                document.title = 'ancestor rejected';
              }
              old.replaceWith();
              old.replaceWith('detached');
            </script>
            """, new() { OnScriptError = errors.Add });
        Assert.Empty(errors);
        Assert.Equal("ancestor rejected", page.Title);
        var main = page.Document.QuerySelector("main")!;
        Assert.Equal("<x>fragmentmove", main.TextContent);
        Assert.Equal(new[] { "next", "move", "end" }, main.Children.Select(element => element.Id));
        Assert.Null(page.Document.GetElementById("old"));
        Assert.Null(main.QuerySelector("x"));
    }

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

    [Fact]
    public async Task ImageSupportsDetachedLoadingThenDomInsertionAndOnlyPublishesTheLatestSource()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentQueue<string>();
        var bytes = Convert.FromBase64String(HtmlRasterTests.Webp);
        await using var server = new BrowserHttpFixture(request =>
        {
            requests.Enqueue(request.Target);
            return request.Target switch
            {
                "/" => Response.Text($$"""
                    <title>start</title><body><script>
                    const img = new Image(12, 34);
                    const valid = img instanceof HTMLImageElement && img instanceof Image && img.tagName === 'IMG' &&
                        img.complete && img.naturalWidth === 0 && img.width === 12 && img.height === 34;
                    const broken = document.createElement('img');
                    broken.addEventListener('error', event => fetch('/outcome?result=' + [valid, event.target === broken,
                        event.bubbles, broken.complete, broken.naturalWidth, img.naturalHeight, img.width,
                        img.isConnected, img.parentElement === document.body].join(',')));
                    img.onerror = () => fetch('/unexpected');
                    img.onload = event => {
                        if (event.bubbles || event.cancelable || event.target !== img || !img.complete ||
                            img.naturalWidth !== 1 || img.currentSrc !== img.src) return;
                        document.body.appendChild(img);
                        broken.setAttribute('src', 'data:image/webp;base64,@@@@');
                    };
                    img.src = '/superseded';
                    img.setAttribute('src', 'data:image/webp;base64,{{HtmlRasterTests.Webp}}');
                    if (img.complete) fetch('/unexpected');
                    </script>
                    """),
                _ when request.Target.StartsWith("/outcome?result=", StringComparison.Ordinal) => Complete(request.Target),
                _ => new("image/webp", bytes)
            };
        });
        using var browser = Trusted(errors);
        await browser.NavigateAsync(server.Root).WaitAsync(TimeSpan.FromSeconds(8));
        var target = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal("/outcome?result=true,true,false,true,0,1,12,true,true", target);
        Assert.DoesNotContain("/superseded", requests);
        Assert.DoesNotContain("/unexpected", requests);
        Assert.Empty(errors);
        var image = Assert.Single(browser.Page!.Document.QuerySelectorAll("img"));
        Assert.Equal(bytes, browser.Page.GetImage(image));

        Response Complete(string target)
        {
            outcome.TrySetResult(target);
            return Response.Text("");
        }
    }

    [Fact]
    public async Task NewImageSourceCanCompleteWhileTheSupersededHttpResponseIsPending()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        var olderRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOlder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var older = Convert.FromBase64String("UklGRhoAAABXRUJQVlA4TA0AAAAvAAAAAIGI6H8g+h8RAA==");
        var newer = Convert.FromBase64String(HtmlRasterTests.Webp);
        await using var server = new BrowserHttpFixture(async (request, token) =>
        {
            switch (request.Target)
            {
                case "/":
                    return Response.Text("""
                        <title>waiting</title><button>Replace</button><img id=image>
                        <script>
                        const image = document.getElementById('image');
                        image.onload = () => {
                            document.title += ':' + image.currentSrc.split('/').pop();
                            fetch('/loaded');
                        };
                        document.querySelector('button').onclick = () => { image.src = '/newer.webp'; };
                        image.src = '/older.webp';
                        </script>
                        """);
                case "/older.webp":
                    olderRequested.TrySetResult();
                    await releaseOlder.Task.WaitAsync(token);
                    return new("image/webp", older);
                case "/newer.webp": return new("image/webp", newer);
                case "/loaded":
                    loaded.TrySetResult();
                    return Response.Text("");
                default: return new("text/plain", [], "404 Not Found");
            }
        });
        using var browser = Trusted(errors);
        await browser.NavigateAsync(server.Root).WaitAsync(TimeSpan.FromSeconds(8));
        await olderRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var page = browser.Page!;
        await page.ActivateAsync(page.Document.QuerySelector("button")!);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(8));
        releaseOlder.TrySetResult();
        await browser.RunAsync(() =>
        {
            Assert.Equal("waiting:newer.webp", page.Title);
            Assert.Equal(newer, page.GetImage(page.Document.GetElementById("image")!));
            return Task.CompletedTask;
        });
        Assert.Empty(errors);
    }

    private static HtmlBrowser Trusted(ConcurrentQueue<HtmlScriptError> errors) => new(new HtmlBrowserOptions
    {
        EnableTrustedScripts = true,
        ScriptOptions = new HtmlPageOptions { OnScriptError = errors.Enqueue }
    });

    private static Response Js(string source) => Response.Text(source, "text/javascript");
}
