using System.Collections.Concurrent;
using System.Security.Cryptography;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using static ShoneHtml.Tests.BrowserHttpFixture;

namespace ShoneHtml.Tests;

public sealed class HtmlFrameworkTests
{
    private const string PreactFileVariable = "SHONEHTML_PREACT_ESM";
    // npm pack preact@10.27.2, package/dist/preact.module.js (unmodified).
    private const string PreactSha256 = "A1CEFABF06EC626ADCB92731537E1E04FD09A7908E22551BAB50540106DC950D";

    [PreactFact]
    public async Task PreactEsmRendersKeyedListAndHandlesFormOverHttp()
    {
        var preact = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable(PreactFileVariable)!);
        Assert.Equal(PreactSha256, Convert.ToHexString(SHA256.HashData(preact)));

        var requests = new ConcurrentQueue<Request>();
        var errors = new ConcurrentQueue<HtmlScriptError>();
        await using var server = new BrowserHttpFixture(request =>
        {
            requests.Enqueue(request);
            return (request.Method, request.Target) switch
            {
                ("GET", "/app/") => Response.Text("""
                    <!doctype html><title>Preact list and form</title><main id="app"></main>
                    <script type="module" src="./app.js"></script>
                    """),
                ("GET", "/app/app.js") => Response.Text(App, "text/javascript"),
                ("GET", "/vendor/preact.module.js") => new("text/javascript", preact),
                _ => new("text/plain", [], "404 Not Found")
            };
        });
        using var browser = new HtmlBrowser(new HtmlBrowserOptions
        {
            EnableTrustedScripts = true,
            RequestTimeout = TimeSpan.FromSeconds(5),
            ScriptOptions = new() { OnScriptError = errors.Enqueue }
        });
        await browser.NavigateAsync(server.Address("app/"));

        await browser.RunAsync(async () =>
        {
            var page = browser.Page;
            Assert.True(page is not null, $"Navigation failed: {browser.Status}; requests: " +
                string.Join(", ", requests.Select(request => $"{request.Method} {request.Target}")));
            IElement Find(string selector) => Assert.IsAssignableFrom<IElement>(page.Document.QuerySelector(selector));
            string[] ListText() => page.Document.QuerySelectorAll("#items li").Select(item => item.TextContent).ToArray();
            void CheckErrors(string stage) => Assert.True(errors.IsEmpty, $"{stage}: " + string.Join("\n", errors.Select(error =>
                $"{error.Source}: {error.Exception.Message}")));

            CheckErrors("ESM import and mount");
            Assert.Contains(requests, r => r.Method == "GET" && r.Target == "/app/app.js");
            Assert.Contains(requests, r => r.Method == "GET" && r.Target == "/vendor/preact.module.js");
            Assert.Equal(new[] { "Alpha", "Beta" }, ListText());
            var alpha = Find("#items li:first-child");
            var beta = Find("#items li:last-child");

            await page.ActivateAsync(Find("#reverse"));
            CheckErrors("Keyed list reorder");
            Assert.Equal(new[] { "Beta", "Alpha" }, ListText());
            Assert.Same(beta, Find("#items li:first-child"));
            Assert.Same(alpha, Find("#items li:last-child"));

            var input = Assert.IsAssignableFrom<IHtmlInputElement>(Find("#item"));
            input.Value = "Gamma";
            page.FormControlChanged(input);
            CheckErrors("Controlled input");
            Assert.Equal("Gamma", Find("#draft").TextContent);

            await page.ActivateAsync(Find("#add"));
            CheckErrors("Form submit");
            Assert.Same(page, browser.Page);
            Assert.DoesNotContain(requests, r => r.Method == "POST");
            Assert.Equal(new[] { "Beta", "Alpha", "Gamma" }, ListText());
            Assert.Equal("", input.Value);
            Assert.Equal("", Find("#draft").TextContent);
        });
    }

    private sealed class PreactFactAttribute : FactAttribute
    {
        public PreactFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PreactFileVariable)))
                Skip = $"Set {PreactFileVariable} to package/dist/preact.module.js extracted from npm pack preact@10.27.2.";
        }
    }

    private const string App = """
        import { Component, h, render } from '../vendor/preact.module.js';

        function ListForm(props) {
          Component.call(this, props);
          this.state = { items: ['Alpha', 'Beta'], draft: '' };
        }

        ListForm.prototype = Object.create(Component.prototype);
        ListForm.prototype.render = function (props, state) {
          return h('section', null,
            h('button', { id: 'reverse', type: 'button', onClick: () => {
              this.setState(prev => ({ items: prev.items.slice().reverse() }));
            } }, 'Reverse'),
            h('ul', { id: 'items' }, state.items.map(item => h('li', { key: item }, item))),
            h('form', { action: './unexpected-submit', method: 'post', onSubmit: event => {
              event.preventDefault();
              this.setState(prev => ({ items: prev.items.concat(prev.draft), draft: '' }));
            } },
              h('label', { for: 'item' }, 'Item'),
              h('input', { id: 'item', name: 'item', value: state.draft, onInput: event => {
                this.setState({ draft: event.currentTarget.value });
              } }),
              h('button', { id: 'add', type: 'submit' }, 'Add')),
            h('span', { id: 'draft' }, state.draft));
        };

        render(h(ListForm), document.getElementById('app'));
        """;
}
