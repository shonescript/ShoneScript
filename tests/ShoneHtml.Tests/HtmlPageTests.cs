// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using AngleSharp.Dom;

namespace ShoneHtml.Tests;

public sealed class HtmlPageTests
{
    [Fact]
    public void ParseRecoversOmittedTagsAndDecodesEntities()
    {
        var page = HtmlPage.Parse("<title>Tea &amp; coffee</title><ul><li id=first>One&nbsp;&lt;<li id=second>&#x4E8C;</ul>");

        Assert.Equal("Tea & coffee", page.Title);
        var items = page.Document.QuerySelectorAll("body > ul > li");
        Assert.Collection(items,
            item => Assert.Equal("One\u00a0<", item.TextContent),
            item => Assert.Equal("二", item.TextContent));
        Assert.Same(items[1], Element(page, "second"));
    }

    [Fact]
    public void ScriptsSeeCompletedDomRunInOrderAndHaveIndependentPageState()
    {
        const string html = """
            <title>Original</title>
            <script>
              const button = document.getElementById('add');
              const list = document.querySelector('#items');
              let count = 0;
              if (window === globalThis && button === document.body.querySelector('button')) {
                document.title = 'Ready';
              }
              Promise.resolve().then(function() { document.title += '!'; });
              button.addEventListener('click', function() {
                const item = document.createElement('li');
                item.id = 'latest';
                item.className = 'entry';
                item.setAttribute('data-count', ++count);
                const oldItem = list.querySelector('.entry');
                if (oldItem) list.removeChild(oldItem);
                if (list.appendChild(item) === item && item.parentElement === list) {
                  item.appendChild(document.createTextNode('Item ' + item.getAttribute('data-count')));
                  item.removeAttribute('data-count');
                }
                Promise.resolve().then(function() { button.textContent = 'Added ' + count; });
              });
            </script>
            <script src="ignored.js">document.title = 'external';</script>
            <script type="module">document.title = 'module';</script>
            <script type="application/json">document.title = 'data';</script>
            <script type=" Text/JavaScript ">document.title += ' Next';</script>
            <button id="add">Add</button><ul id="items"></ul>
            """;
        List<HtmlScriptError> errors = [];
        var options = new HtmlPageOptions { OnScriptError = errors.Add };
        var first = HtmlPage.Parse(html, options);
        var second = HtmlPage.Parse(html, options);
        var button = Element(first, "add");

        Assert.Empty(errors);
        Assert.Equal("Ready! Next", first.Title);
        Assert.Equal(first.Title, second.Title);
        Assert.Equal(1, first.Version);
        List<(long Version, string Text)> notifications = [];
        first.Changed += () => notifications.Add((first.Version, button.TextContent));

        first.Click(button);
        var firstItem = Element(first, "latest");
        Assert.Equal("Item 1", firstItem.TextContent);
        Assert.Equal("entry", firstItem.ClassName);
        Assert.False(firstItem.HasAttribute("data-count"));

        first.Click(button);
        Assert.Null(firstItem.Parent);
        Assert.Equal("Item 2", Assert.Single(Element(first, "items").Children).TextContent);
        Assert.Equal(new[] { (2L, "Added 1"), (3L, "Added 2") }, notifications);

        Assert.Empty(Element(second, "items").Children);
        Assert.Equal("Add", Element(second, "add").TextContent);
        second.Click(Element(second, "add"));
        Assert.Equal("Item 1", Element(second, "latest").TextContent);
        Assert.Empty(errors);
    }

    [Fact]
    public void DisabledScriptsLeaveTheDomUnchanged()
    {
        List<HtmlScriptError> errors = [];
        var page = HtmlPage.Parse("""
            <title>Safe</title><button id="go">Go</button>
            <script>
              document.title = 'Changed';
              document.getElementById('go').addEventListener('click', function() { document.title = 'Clicked'; });
              throw new Error('script ran');
            </script>
            """, new HtmlPageOptions { EnableScripts = false, OnScriptError = errors.Add });
        var notifications = 0;
        page.Changed += () => notifications++;

        page.Click(Element(page, "go"));

        Assert.Equal("Safe", page.Title);
        Assert.Equal(0, page.Version);
        Assert.Equal(0, notifications);
        Assert.Empty(errors);
    }

    [Fact]
    public void ClickSnapshotsListenersAndAncestorPathDuringDispatch()
    {
        List<HtmlScriptError> errors = [];
        var page = HtmlPage.Parse("""
            <div id="parent"><button id="child">Go</button></div><div id="new-parent"></div>
            <output id="log"></output>
            <script>
              const child = document.getElementById('child');
              const parent = document.getElementById('parent');
              const newParent = document.getElementById('new-parent');
              const log = document.getElementById('log');
              function record(label, e, receiver, expected) {
                if (e.type !== 'click' || e.target !== child || e.currentTarget !== expected || receiver !== expected) {
                  throw new Error('bad event receiver');
                }
                log.textContent += label;
              }
              function late(e) { record('late;', e, this, child); }
              function removed() { log.textContent += 'removed;'; }
              function first(e) {
                record('first;', e, this, child);
                child.addEventListener('click', late);
                child.removeEventListener('click', removed);
                newParent.appendChild(child);
                Promise.resolve().then(function() { if (e.currentTarget === null) log.textContent += 'job;'; });
              }
              child.addEventListener('click', first);
              child.addEventListener('click', first);
              child.addEventListener('click', removed);
              parent.addEventListener('click', function(e) { record('old;', e, this, parent); });
              newParent.addEventListener('click', function(e) { record('new;', e, this, newParent); });
              document.addEventListener('click', function(e) { record('document;', e, this, document); });
              window.addEventListener('click', function(e) { record('window;', e, this, window); });
            </script>
            """, new HtmlPageOptions { OnScriptError = errors.Add });
        var child = Element(page, "child");
        var log = Element(page, "log");

        page.Click(child);
        Assert.Empty(errors);
        Assert.Equal("first;old;document;window;job;", log.TextContent);
        Assert.Same(Element(page, "new-parent"), child.ParentElement);

        page.Click(child);
        Assert.Equal("first;old;document;window;job;first;late;new;document;window;job;", log.TextContent);
        Assert.Empty(errors);
    }

    [Fact]
    public void StopPropagationAllowsOtherListenersOnTheSameTarget()
    {
        List<HtmlScriptError> errors = [];
        var page = HtmlPage.Parse("""
            <div id="parent"><button id="child">Start</button></div>
            <script>
              const child = document.getElementById('child');
              child.addEventListener('click', function(e) { e.stopPropagation(); child.textContent = 'First'; });
              child.addEventListener('click', function() { child.textContent += ' second'; });
              child.parentElement.addEventListener('click', function() { child.textContent += ' parent'; });
              window.addEventListener('click', function() { child.textContent += ' window'; });
            </script>
            """, new HtmlPageOptions { OnScriptError = errors.Add });
        var child = Element(page, "child");

        page.Click(child);

        Assert.Empty(errors);
        Assert.Equal("First second", child.TextContent);
        Assert.Equal(1, page.Version);
    }

    [Fact]
    public void SyntaxAndRuntimeErrorsReportSourcesAndLaterWorkContinues()
    {
        List<HtmlScriptError> errors = [];
        var page = HtmlPage.Parse("""
            <button id="go">Go</button>
            <script>let = ;</script>
            <script>throw new Error('load failed');</script>
            <script>
              const button = document.getElementById('go');
              button.addEventListener('click', function() { this.textContent = 'Started'; throw new Error('click failed'); });
              button.addEventListener('click', function() { this.textContent += ' and finished'; });
            </script>
            """, new HtmlPageOptions { OnScriptError = errors.Add });

        var button = Element(page, "go");
        page.Click(button);

        Assert.Equal("Started and finished", button.TextContent);
        Assert.Equal(1, page.Version);
        Assert.Collection(errors,
            error => Assert.Equal("script[1]", error.Source),
            error => AssertError(error, "script[2]", "load failed"),
            error => AssertError(error, "click", "click failed"));
    }

    [Fact]
    public void LoopBudgetInterruptsTopLevelAndClickScripts()
    {
        List<HtmlScriptError> errors = [];
        var page = HtmlPage.Parse("""
            <button id="go">Go</button>
            <script>
              for (let i = 0; i < 100; i++) { }
              document.title = 'loop completed';
            </script>
            <script>
              const button = document.getElementById('go');
              button.addEventListener('click', function() {
                for (let i = 0; i < 100; i++) { }
                button.textContent = 'event loop completed';
              });
              button.addEventListener('click', function() { button.textContent += '!'; });
            </script>
            """, new HtmlPageOptions { MaximumLoopIterations = 8, OnScriptError = errors.Add });

        var button = Element(page, "go");
        page.Click(button);

        Assert.Collection(errors,
            error => AssertError(error, "script[1]", "limit"),
            error => AssertError(error, "click", "limit"));
        Assert.Equal("", page.Title);
        Assert.Equal("Go!", button.TextContent);
    }

    [Fact]
    public void PromiseJobBudgetInterruptsAChainAndReportsItsCheckpoint()
    {
        List<HtmlScriptError> errors = [];
        var page = HtmlPage.Parse("""
            <script>
              let jobs = 0;
              function again() {
                document.title = String(++jobs);
                if (jobs < 100) Promise.resolve().then(again);
              }
              Promise.resolve().then(again);
            </script>
            """, new HtmlPageOptions { MaximumLoopIterations = 8, OnScriptError = errors.Add });

        Assert.Equal("8", page.Title);
        Assert.Equal(1, page.Version);
        AssertError(Assert.Single(errors), "script[1] Promise jobs", "limit");
    }

    [Fact]
    public void OuterHtmlReplacesInParentContextAndPreservesDetachedReferences()
    {
        List<HtmlScriptError> errors = [];
        using var page = HtmlPage.Parse("""
            <table><tbody><tr id="old"><td>Old</td></tr><tr id="last"><td>Last</td></tr></tbody></table>
            <div id="host"><span id="remove">Remove</span></div>
            <script>
                const old = document.getElementById('old');
                old.outerHTML = '<tr id="first"><td>First</td></tr><tr id="second"><td>Second</td></tr>';
                old.outerHTML = '<tr id="ignored"></tr>';
                const host = document.getElementById('host');
                const fragment = document.createDocumentFragment();
                const child = document.createElement('div');
                fragment.appendChild(child);
                child.outerHTML = '<span id="fragment">Fragment</span>tail';
                host.appendChild(fragment);
                document.getElementById('remove').outerHTML = null;
                if (!old.isConnected && old.outerHTML.indexOf('Old') >= 0 && !child.isConnected) {
                    try { document.documentElement.outerHTML = ''; }
                    catch (error) { document.title = error.name; }
                }
            </script>
            """, new HtmlPageOptions { OnScriptError = errors.Add });

        Assert.Empty(errors);
        Assert.Equal("NoModificationAllowedError", page.Title);
        Assert.Equal(new[] { "first", "second", "last" }, page.Document.QuerySelectorAll("tbody > tr").Select(tr => tr.Id));
        Assert.Equal("First", Element(page, "first").TextContent);
        Assert.Null(page.Document.GetElementById("old"));
        Assert.Null(page.Document.GetElementById("ignored"));
        Assert.Null(page.Document.GetElementById("remove"));
        Assert.Equal("Fragmenttail", Element(page, "host").TextContent);
    }

    [Fact]
    public async Task OuterHtmlLimitsRejectUncommittedMutations()
    {
        await using var server = new BrowserHttpFixture(_ => BrowserHttpFixture.Response.Text("""
            <title>limits:</title><main><div id="target">original</div></main>
            <script>
                const target = document.getElementById('target');
                function attempt(markup, kind) {
                    try { target.outerHTML = markup; }
                    catch (error) {
                        if (error.name === 'TypeError' && target.isConnected) document.title += kind;
                    }
                }
                let nodes = '', deep = '', big = 'abc', unicode = '\u4e00';
                for (let i = 0; i < 40; i++) nodes += '<i></i>';
                for (let i = 0; i < 12; i++) deep = '<div>' + deep + '</div>';
                for (let i = 0; i < 10; i++) { big += big; unicode += unicode; }
                attempt(big, ' bytes');
                attempt(unicode, ' utf8');
                attempt(deep, ' depth');
                attempt(nodes, ' nodes');
            </script>
            """));
        using var browser = new HtmlBrowser(new HtmlBrowserOptions
        {
            EnableTrustedScripts = true, MaximumResponseBytes = 2048, MaximumDocumentDepth = 10, MaximumDocumentNodes = 30
        });
        await browser.NavigateAsync(server.Root);

        var page = Assert.IsType<HtmlPage>(browser.Page);
        Assert.Equal("limits: bytes utf8 depth nodes", page.Title);
        Assert.Equal("original", Element(page, "target").TextContent);
        Assert.Single(page.Document.QuerySelectorAll("main > *"));
    }

    private static IElement Element(HtmlPage page, string id) =>
        Assert.IsAssignableFrom<IElement>(page.Document.GetElementById(id));

    private static void AssertError(HtmlScriptError error, string source, string message)
    {
        Assert.Equal(source, error.Source);
        Assert.Contains(message, error.Exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
