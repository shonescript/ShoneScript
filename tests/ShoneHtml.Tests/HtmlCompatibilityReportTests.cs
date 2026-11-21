using System.Collections.Concurrent;
using System.Diagnostics;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using static ShoneHtml.Tests.BrowserHttpFixture;

namespace ShoneHtml.Tests;

public sealed class HtmlCompatibilityReportTests
{
    private const string SnapshotFileVariable = "SHONEHTML_COMPATIBILITY_SNAPSHOT";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task TrustedReportSettlesEveryGroupAndControlsWorkThroughActivation()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        var requests = new ConcurrentQueue<string>();
        await using var server = StartReport(requests);
        using var browser = CreateTrustedBrowser(errors);

        await browser.NavigateAsync(server.Root).WaitAsync(TestTimeout);
        await browser.RunAsync(async () =>
        {
            AssertNoErrors(errors);
            var page = Assert.IsType<HtmlPage>(browser.Page);
            AssertReport(page, "1", filtered: false);
            Assert.All(Groups(page), group => Assert.True(group.HasAttribute("open")));
            var firstGroup = Groups(page)[0];

            var summary = Find(page, "#results details:first-child summary");
            await page.ActivateAsync(summary);
            Assert.False(firstGroup.HasAttribute("open"));
            await page.ActivateAsync(summary);
            Assert.True(firstGroup.HasAttribute("open"));

            await page.ActivateAsync(Find(page, "#toggle"));
            Assert.All(Groups(page), group => Assert.False(group.HasAttribute("open")));
            await page.ActivateAsync(Find(page, "#toggle"));
            Assert.All(Groups(page), group => Assert.True(group.HasAttribute("open")));

            var filter = Assert.IsAssignableFrom<IHtmlInputElement>(Find(page, "#filter"));
            await page.ActivateAsync(filter);
            Assert.True(filter.IsChecked);
            AssertReport(page, "1", filtered: true);

            for (var run = 2; run <= 3; run++)
            {
                await page.ActivateAsync(Find(page, "#rerun"));
                AssertNoErrors(errors);
                Assert.Same(page, browser.Page);
                AssertReport(page, run.ToString(), filtered: true);
                Assert.All(Groups(page), group => Assert.True(group.HasAttribute("open")));
            }
            Assert.NotSame(firstGroup, Groups(page)[0]);

            await page.ActivateAsync(filter);
            Assert.False(filter.IsChecked);
            AssertReport(page, "3", filtered: false);
            AssertNoErrors(errors);
        }).WaitAsync(TestTimeout);

        Assert.Equal(new[] { "/", "/report.js" }, requests.ToArray());
    }

    [Fact]
    public async Task OverBudgetRerunsAreStoppedAndLeaveTheReportInteractive()
    {
        var errors = new ConcurrentQueue<HtmlScriptError>();
        await using var server = StartReport();
        using var browser = CreateTrustedBrowser(errors);

        await browser.NavigateAsync(server.Address("budget")).WaitAsync(TestTimeout);
        await browser.RunAsync(async () =>
        {
            AssertNoErrors(errors);
            var page = Assert.IsType<HtmlPage>(browser.Page);
            AssertReport(page, "1", filtered: false);
            var previousGroup = Groups(page)[0];
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await page.ActivateAsync(Find(page, "#rerun"));
                var error = Assert.Single(errors.ToArray());
                Assert.Equal("click", error.Source);
                Assert.Contains("limit", error.Exception.Message, StringComparison.OrdinalIgnoreCase);
                errors.Clear();
                AssertReport(page, "1", filtered: false);
                Assert.Same(previousGroup, Groups(page)[0]);
            }

            await page.ActivateAsync(Find(page, "#filter"));
            AssertReport(page, "1", filtered: true);
            AssertNoErrors(errors);
        }).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DefaultBrowserDoesNotDownloadOrExecuteReportScripts()
    {
        var requests = new ConcurrentQueue<string>();
        await using var server = StartReport(requests);
        using var browser = new HtmlBrowser();

        await browser.NavigateAsync(server.Root).WaitAsync(TestTimeout);
        await browser.RunAsync(async () =>
        {
            var page = Assert.IsType<HtmlPage>(browser.Page);
            await page.ActivateAsync(Find(page, "#rerun"));
            Assert.Equal("Compatibility report", page.Title);
            Assert.Equal("Pending", Find(page, "#score").TextContent);
            Assert.Empty(Groups(page));
            Assert.Null(Find(page, "#results").GetAttribute("data-run"));
        }).WaitAsync(TestTimeout);

        Assert.Equal("/", Assert.Single(requests));
        Assert.Contains("Page scripts were not run", browser.Status);
        await browser.NavigateAsync(server.Address("/#results")).WaitAsync(TestTimeout);
        Assert.Contains("Page scripts were not run", browser.Status);
    }

    [CompatibilitySnapshotFact]
    public async Task TrustedLocalSnapshotSettlesAndReruns()
    {
        var snapshot = await File.ReadAllTextAsync(Environment.GetEnvironmentVariable(SnapshotFileVariable)!);
        var inlineScripts = new HtmlParser().ParseDocument(snapshot).QuerySelectorAll("script:not([src])");
        var reportScript = Assert.Single(inlineScripts.Select(script => script.TextContent),
            script => script.Contains("h5-results", StringComparison.Ordinal) &&
                      script.Contains("h5-rerun", StringComparison.Ordinal) &&
                      script.Contains("h5-score", StringComparison.Ordinal));
        // Use only the explicitly trusted report script; the saved site's shell and linked assets are not loaded.
        await using var server = new BrowserHttpFixture(request => request.Target switch
        {
            "/" => Response.Text("""
                <!doctype html><p id=h5-score></p><p id=h5-max></p><i id=h5-prog></i>
                <p id=h5-env></p><p id=h5-count></p><div id=h5-results></div>
                <input id=h5-only type=checkbox><button id=h5-toggle>Toggle</button><button id=h5-rerun>Rerun</button>
                <button id=h5-copy>Copy</button><button id=h5-json>JSON</button>
                <script src=/snapshot.js></script>
                """),
            "/snapshot.js" => Response.Text(reportScript, "text/javascript"),
            _ => new Response("text/plain", [], "404 Not Found")
        });
        var errors = new ConcurrentQueue<HtmlScriptError>();
        using var browser = new HtmlBrowser(new HtmlBrowserOptions
        {
            EnableTrustedScripts = true,
            ScriptOptions = new() { OnScriptError = errors.Enqueue }
        });
        await browser.NavigateAsync(server.Root).WaitAsync(TestTimeout);
        var first = await WaitForReportAsync();
        await browser.RunAsync(() =>
        {
            var page = Assert.IsType<HtmlPage>(browser.Page);
            var layout = new HtmlLayout().Get(page.Document, page.Version, 960,
                (text, style) => new Aprillz.MewUI.Size(text.Length * style.FontSize / 2, style.FontSize), 680);
            Assert.Contains(layout.Items, item => ReferenceEquals(item.Element, first.QuerySelector(".h5-name")) &&
                !string.IsNullOrWhiteSpace(item.Text));
            return Task.CompletedTask;
        }).WaitAsync(TestTimeout);
        await browser.RunAsync(() =>
        {
            var page = Assert.IsType<HtmlPage>(browser.Page);
            return page.ActivateAsync(Find(page, "#h5-rerun"));
        }).WaitAsync(TestTimeout);
        Assert.NotSame(first, await WaitForReportAsync());

        async Task<IElement> WaitForReportAsync()
        {
            var timer = Stopwatch.StartNew();
            IElement? firstRow = null;
            do
            {
                await browser.RunAsync(() =>
                {
                    AssertNoErrors(errors);
                    var page = Assert.IsType<HtmlPage>(browser.Page);
                    var rows = page.Document.QuerySelectorAll("#h5-results .h5-row");
                    if (rows.Length == 0 || rows.Any(row => !row.HasAttribute("data-r")))
                        return Task.CompletedTask;
                    Assert.True(int.TryParse(Find(page, "#h5-score").TextContent, out var score));
                    Assert.True(int.TryParse(Find(page, "#h5-max").TextContent, out var max));
                    Assert.InRange(score, 1, max);
                    Assert.True(max >= rows.Length);
                    var groups = page.Document.QuerySelectorAll("#h5-results details");
                    Assert.True(groups.Length > 1);
                    Assert.All(groups, group =>
                    {
                        Assert.NotNull(group.QuerySelector("summary"));
                        Assert.NotEmpty(group.QuerySelectorAll(".h5-row"));
                    });
                    Assert.All(rows, row => Assert.Contains(row.GetAttribute("data-r"), new[] { "y", "p", "n" }));
                    firstRow = rows[0];
                    return Task.CompletedTask;
                }).WaitAsync(TestTimeout);
                if (firstRow is not null)
                    return firstRow;
                await Task.Delay(25);
            } while (timer.Elapsed < TestTimeout);
            throw new TimeoutException("The compatibility snapshot did not settle all probes.");
        }
    }

    private sealed class CompatibilitySnapshotFactAttribute : FactAttribute
    {
        public CompatibilitySnapshotFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SnapshotFileVariable)))
                Skip = $"Set {SnapshotFileVariable} to trusted local HTML from the ebtool compatibility report.";
        }
    }

    private static HtmlBrowser CreateTrustedBrowser(ConcurrentQueue<HtmlScriptError> errors) => new(new HtmlBrowserOptions
    {
        EnableTrustedScripts = true,
        ScriptOptions = new() { MaximumLoopIterations = 128, OnScriptError = errors.Enqueue }
    });

    private static void AssertNoErrors(ConcurrentQueue<HtmlScriptError> errors) =>
        Assert.True(errors.IsEmpty, string.Join(Environment.NewLine, errors.Select(error => $"{error.Source}: {error.Exception}")));

    private static void AssertReport(HtmlPage page, string run, bool filtered)
    {
        Assert.Equal("7 / 13", Find(page, "#score").TextContent);
        var totals = Find(page, "#totals");
        Assert.Equal("5 probes: 2 supported, 1 partial, 2 unsupported", totals.TextContent);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", totals.GetAttribute("data-generated") ?? "");
        Assert.Equal("width: 50.0%;", Find(page, "#progress").GetAttribute("style"));
        Assert.Equal(run, Find(page, "#results").GetAttribute("data-run"));
        Assert.Collection(Groups(page),
            group => AssertGroup(group, "Document (1/2)", ("Element query", "supported"), ("Unknown capability", "unsupported")),
            group => AssertGroup(group, "Script (1/1)", ("Version pattern", "supported")),
            group => AssertGroup(group, "Async (0/2)", ("Partial result", "partial"), ("Rejected probe", "unsupported")));
        Assert.Empty(page.Document.QuerySelectorAll("#results .pending"));
        Assert.All(page.Document.QuerySelectorAll("#results .probe"), row =>
            Assert.Equal(filtered && row.GetAttribute("data-state") == "supported", row.HasAttribute("hidden")));
    }

    private static void AssertGroup(IElement group, string heading, params (string Name, string State)[] expected)
    {
        Assert.Equal(heading, group.QuerySelector("summary")?.TextContent);
        Assert.Equal(expected, group.QuerySelectorAll(".probe").Select(row =>
        {
            var state = row.GetAttribute("data-state");
            var badge = Assert.IsAssignableFrom<IElement>(row.QuerySelector("strong"));
            Assert.Equal(state, badge.TextContent);
            return (row.QuerySelector("span")!.TextContent, state!);
        }).ToArray());
    }

    private static IElement[] Groups(HtmlPage page) => page.Document.QuerySelectorAll("#results details").ToArray();
    private static IElement Find(HtmlPage page, string selector) => Assert.IsAssignableFrom<IElement>(page.Document.QuerySelector(selector));

    private static BrowserHttpFixture StartReport(ConcurrentQueue<string>? requests = null) => new(request =>
    {
        requests?.Enqueue(request.Target);
        return request.Target switch
        {
            "/" => Response.Text(ReportPage),
            "/budget" => Response.Text(ReportPage.Replace("/report.js", "/report.js?exhaust", StringComparison.Ordinal)),
            "/report.js" => Response.Text("const exhaust = false;\n" + ReportScript, "text/javascript"),
            "/report.js?exhaust" => Response.Text("const exhaust = true;\n" + ReportScript, "text/javascript"),
            _ => new Response("text/plain", [], "404 Not Found")
        };
    });

    private const string ReportPage = """
        <!doctype html><title>Compatibility report</title>
        <p id=score>Pending</p><p id=totals></p><div id=progress></div>
        <label><input id=filter type=checkbox> Hide supported probes</label>
        <button id=toggle type=button>Expand or collapse</button>
        <button id=rerun type=button>Rerun</button>
        <div id=results></div>
        <script>document.title = 'Scripts enabled';</script>
        <script src=/report.js></script>
        """;

    private const string ReportScript = """
        const find = id => document.getElementById(id);
        const results = find('results');
        const filter = find('filter');
        let runs = 0;
        const probes = [
          ['Document', [
            ['Element query', 3, () => !!document.querySelector('#score')],
            ['Unknown capability', 1, () => 'showSaveFilePicker' in window]
          ]],
          ['Script', [
            ['Version pattern', 2, () => (/^report\/(\d+)$/i.exec('REPORT/12') || [])[1] === '12']
          ]],
          ['Async', [
            ['Partial result', 3, () => Promise.resolve('partial')],
            ['Rejected probe', 4, () => Promise.reject('unavailable')]
          ]]
        ];

        function applyFilter() {
          results.querySelectorAll('.probe').forEach(row => {
            row.hidden = filter.checked && row.getAttribute('data-state') === 'supported';
          });
        }
        function run() {
          runs++;
          if (exhaust && runs > 1) {
            for (let i = 0; i < 10_000; i++) { }
          }
          results.innerHTML = '';
          results.setAttribute('data-run', String(runs));
          let earned = 0, max = 0, yes = 0, partial = 0, no = 0, total = 0;
          probes.forEach(group => {
            const section = document.createElement('details');
            section.open = true;
            const heading = document.createElement('summary');
            section.appendChild(heading);
            results.appendChild(section);
            let supported = 0;
            group[1].forEach(probe => {
              const row = document.createElement('div');
              row.className = 'probe';
              row.innerHTML = '<span>' + probe[0] + '</span><i class="pending">Pending</i>';
              section.appendChild(row);
              max += probe[1];
              total++;
              function settle(value) {
                let state = 'unsupported';
                if (value === true) {
                  state = 'supported'; earned += probe[1]; supported++; yes++;
                } else if (value === 'partial') {
                  state = value; earned += probe[1] / 2; partial++;
                } else { no++; }
                heading.textContent = group[0] + ' (' + supported + '/' + group[1].length + ')';
                row.setAttribute('data-state', state);
                row.querySelector('.pending').outerHTML = '<strong>' + state + '</strong>';
              }
              const value = probe[2]();
              if (value && typeof value.then === 'function') value.then(settle, () => settle(false));
              else settle(value);
            });
          });
          Promise.resolve().then(() => {
            find('score').textContent = Math.round(earned) + ' / ' + max;
            const totals = find('totals');
            totals.textContent = total + ' probes: ' + yes + ' supported, ' + partial + ' partial, ' + no + ' unsupported';
            totals.setAttribute('data-generated', new Date().toISOString());
            find('progress').style.width = (100 * earned / max).toFixed(1) + '%';
            applyFilter();
          });
        }
        filter.addEventListener('change', applyFilter);
        find('toggle').addEventListener('click', () => {
          const groups = results.querySelectorAll('details');
          const open = !groups[0].open;
          groups.forEach(group => { group.open = open; });
        });
        find('rerun').addEventListener('click', run);
        run();
        """;
}
