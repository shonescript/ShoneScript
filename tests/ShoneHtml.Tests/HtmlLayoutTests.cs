// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AngleSharp.Dom;
using Aprillz.MewUI;
using Xunit.Abstractions;

namespace ShoneHtml.Tests;

public sealed class HtmlLayoutTests(ITestOutputHelper output)
{
    [Fact]
    public void CascadeAppliesDescendantsSpecificitySourceOrderAndInheritance()
    {
        var page = HtmlPage.Parse("""
            <style>
                #specific { color: red; }
                .panel { font-family: 'Test Font'; font-size: 20px; }
                .panel span { color: blue; font-weight: bold; }
                .panel span { color: green; }
                .force { color: #663399 !important; }
            </style>
            <section class="panel">
                <span id="specific"><em>specific</em></span>
                <span>later</span>
                <span style="color: #123456">inline</span>
                <span class="force" style="color: black">important</span>
            </section>
            <span>outside</span>
            """);
        var result = new HtmlLayout().Get(page.Document, page.Version, 500, Measure);

        var specific = TextItem(result, "specific");
        Assert.Equal(Color.FromRgb(255, 0, 0), specific.TextStyle.Color);
        Assert.Equal("Test Font", specific.TextStyle.FontFamily);
        Assert.Equal(20, specific.TextStyle.FontSize);
        Assert.True(specific.TextStyle.Bold);
        Assert.True(specific.TextStyle.Italic);
        Assert.Equal(Color.FromRgb(0, 128, 0), TextItem(result, "later").TextStyle.Color);
        Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), TextItem(result, "inline").TextStyle.Color);
        Assert.Equal(Color.FromRgb(0x66, 0x33, 0x99), TextItem(result, "important").TextStyle.Color);
        Assert.Equal(HtmlTextStyle.Default, TextItem(result, "outside").TextStyle);
    }

    [Fact]
    public void BoxSizingAndHiddenSubtreeAffectPaintAndHitTesting()
    {
        var page = HtmlPage.Parse("""
            <style>body { margin: 0; } .hidden { display: none; }</style>
            <div id="box" style="box-sizing: border-box; width: 80px; height: 40px; margin: 5px; padding: 4px; border: 2px solid red; background: #abc">Label</div>
            <div class="hidden"><button id="hidden">invisible</button></div>
            """);
        var result = new HtmlLayout().Get(page.Document, page.Version, 120, Measure);
        var box = Assert.Single(result.Items, item => item.Element.Id == "box" && item.Text is null);
        var label = TextItem(result, "Label");

        Assert.Equal(new Rect(5, 5, 80, 40), box.Bounds);
        Assert.Equal(Color.FromRgb(0xaa, 0xbb, 0xcc), box.Background);
        Assert.Equal(Color.FromRgb(255, 0, 0), box.BorderColor);
        Assert.Equal(2, box.BorderWidth);
        Assert.Equal(11, label.Bounds.Left);
        Assert.Equal(11, label.Bounds.Top);
        Assert.Same(box.Element, result.HitTest(6, 6));
        Assert.DoesNotContain(result.Items, item => item.Element.Id == "hidden" || item.Text == "invisible");
    }

    [Theory]
    [InlineData("中文换行测试文本")]
    [InlineData("abcdefghijklmnopqrstuvwxyz")]
    [InlineData("🙂🙂🙂🙂🙂🙂🙂")]
    public void NarrowFlowWrapsLongTextAndPreservesItsCharacters(string text)
    {
        var page = HtmlPage.Parse($"<style>body {{ margin: 0; }}</style><div id='line'>{text}</div>");
        var layout = new HtmlLayout();
        var narrow = layout.Get(page.Document, page.Version, 30, Measure);
        var wide = layout.Get(page.Document, page.Version, 400, Measure);
        var runs = narrow.Items.Where(item => item.Element.Id == "line" && item.Text is not null).ToArray();

        Assert.Equal(text, string.Concat(runs.Select(item => item.Text)));
        Assert.True(runs.Select(item => item.Bounds.Top).Distinct().Count() > 1);
        Assert.All(runs, item => Assert.InRange(item.Bounds.Right, 1, 30));
        Assert.Equal(0, TextItem(wide, text).Bounds.Top);
        Assert.True(narrow.Height > wide.Height);
    }

    [Fact]
    public void ScrolledHitUsesTopmostBoxEvenWhenBoxesOverlap()
    {
        var page = HtmlPage.Parse("""
            <style>body { margin: 0; } button { display: block; box-sizing: border-box; width: 80px; height: 40px; }</style>
            <div style="height: 300px"></div>
            <button id="under">Under</button><button id="over" style="margin-top: -20px">Over</button>
            <script>
              const over = document.getElementById('over');
              over.addEventListener('click', function() { this.textContent = 'Clicked'; });
            </script>
            """);
        var layout = new HtmlLayout();
        var result = layout.Get(page.Document, page.Version, 100, Measure);
        var under = page.Document.GetElementById("under");
        var over = page.Document.GetElementById("over");
        const double scroll = 312;
        const double pointerY = 12;

        Assert.Same(under, result.HitTest(2, 310));
        var hit = Assert.IsAssignableFrom<IElement>(result.HitTest(2, pointerY + scroll));
        Assert.Same(over, hit);
        Assert.Equal(new[] { "under", "over" }, result.GetVisibleItems(scroll, scroll + 30)
            .Where(item => item.Text is null && item.Element.LocalName == "button").Select(item => item.Element.Id));
        Assert.Null(result.HitTest(2, result.Height + 1));

        page.Click(hit);

        var updated = layout.Get(page.Document, page.Version, 100, Measure);
        Assert.NotSame(result, updated);
        Assert.Same(hit, TextItem(updated, "Clicked").Element);
    }

    [Fact]
    public void CacheTracksVersionWidthDocumentAndMeasurementDelegate()
    {
        var page = HtmlPage.Parse("""
            <style id="sheet">body { margin: 0; } #label { font-size: 16px; }</style>
            <div id="label">Sample</div><button id="go">Go</button>
            <script>
              document.getElementById('go').addEventListener('click', function() {
                document.getElementById('sheet').textContent = 'body { margin: 0; } #label { font-size: 32px; }';
              });
            </script>
            """);
        var layout = new HtmlLayout();
        List<(string Text, double FontSize)> measurements = [];
        Size CountedMeasure(string text, HtmlTextStyle style)
        {
            measurements.Add((text, style.FontSize));
            return Measure(text, style);
        }

        var first = layout.Get(page.Document, page.Version, 140, CountedMeasure);
        var firstCount = measurements.Count;
        Assert.Contains(("Sample", 16), measurements);
        Assert.Same(first, layout.Get(page.Document, page.Version, 140, CountedMeasure));
        var resized = layout.Get(page.Document, page.Version, 120, CountedMeasure);
        Assert.NotSame(first, resized);
        Assert.Equal(firstCount, measurements.Count);

        page.Click(Assert.IsAssignableFrom<IElement>(page.Document.GetElementById("go")));
        var restyled = layout.Get(page.Document, page.Version, 120, CountedMeasure);
        Assert.NotSame(resized, restyled);
        Assert.Equal(32, TextItem(restyled, "Sample").TextStyle.FontSize);
        Assert.Contains(("Sample", 32), measurements);

        var other = HtmlPage.Parse("<div>Sample</div>");
        var replaced = layout.Get(other.Document, page.Version, 90, CountedMeasure);
        Assert.NotSame(restyled, replaced);
        Assert.Same(other.Document.Body?.FirstElementChild, TextItem(replaced, "Sample").Element);

        layout.Clear();
        measurements.Clear();
        var rebuilt = layout.Get(other.Document, page.Version, 90, CountedMeasure);
        Assert.NotSame(replaced, rebuilt);
        Assert.Contains(("Sample", 16), measurements);

        var differentMeasurer = layout.Get(other.Document, page.Version, 90, (text, _) => new Size(text.Length * 5, 12));
        Assert.NotSame(rebuilt, differentMeasurer);
        Assert.Equal(30, TextItem(differentMeasurer, "Sample").Bounds.Width);
    }

    [Fact]
    public void MeasurementCacheEvictsEntriesAcrossManyDistinctLabels()
    {
        const int rowCount = 4096;
        var html = new StringBuilder("<style>body { margin: 0; }</style>");
        for (var i = 0; i < rowCount; i++)
            html.Append("<div>Row").Append(i).Append("</div>");
        var page = HtmlPage.Parse(html.ToString(), new HtmlPageOptions { EnableScripts = false });
        var layout = new HtmlLayout();
        HashSet<string> labels = [];
        Size CountedMeasure(string text, HtmlTextStyle style)
        {
            labels.Add(text);
            return Measure(text, style);
        }

        var first = layout.Get(page.Document, page.Version, 320, CountedMeasure);
        Assert.Equal(rowCount, labels.Count);
        labels.Clear();
        var resized = layout.Get(page.Document, page.Version, 319, CountedMeasure);

        Assert.Equal(first.Items.Count, resized.Items.Count);
        Assert.NotEmpty(labels);
    }

    [Fact]
    public void DeepUnclosedMarkupCanBeParsedAndLaidOut()
    {
        const int depth = 2048;
        var html = new StringBuilder("<style>body { margin: 0; }</style>");
        for (var i = 0; i < depth; i++) html.Append("<div><span>");
        html.Append("<b id='bottom'>End");
        var page = HtmlPage.Parse(html.ToString(), new HtmlPageOptions { EnableScripts = false });
        var result = new HtmlLayout().Get(page.Document, page.Version, 500, Measure);
        var end = TextItem(result, "End");

        Assert.Equal("bottom", end.Element.Id);
        Assert.True(end.TextStyle.Bold);
        Assert.Same(end.Element, result.HitTest(end.Bounds.Left + 1, end.Bounds.Top + 1));
    }

    [Fact]
    public void LargeStaticDocumentReusesLayoutAndMeasuresRepeatedTextOnce()
    {
        const int rowCount = 3000;
        var html = new StringBuilder("<style>body { margin: 0; } .row { height: 20px; }</style>");
        for (var i = 0; i < rowCount; i++) html.Append("<div class='row'>Repeat</div>");
        var clock = Stopwatch.StartNew();
        var page = HtmlPage.Parse(html.ToString(), new HtmlPageOptions { EnableScripts = false });
        var parseTime = clock.Elapsed;
        var layout = new HtmlLayout();
        var calls = new Dictionary<string, int>();
        Size CountedMeasure(string text, HtmlTextStyle style)
        {
            calls[text] = calls.GetValueOrDefault(text) + 1;
            return Measure(text, style);
        }

        clock.Restart();
        var first = layout.Get(page.Document, page.Version, 320, CountedMeasure);
        var layoutTime = clock.Elapsed;
        var firstMeasureCount = calls.Values.Sum();
        Assert.Equal(rowCount, first.Items.Count(item => item.Text == "Repeat"));
        Assert.Equal(1, calls["Repeat"]);
        Assert.Equal(rowCount * 20, first.Height);

        clock.Restart();
        for (var i = 0; i < 50; i++)
        {
            var redraw = layout.Get(page.Document, page.Version, 320, CountedMeasure);
            Assert.Same(first, redraw);
            var top = redraw.Height - (i + 3) * 20;
            Assert.Equal(3, redraw.GetVisibleItems(top, top + 60).Count(item => item.Text == "Repeat"));
        }
        var redrawTime = clock.Elapsed;
        Assert.Equal(firstMeasureCount, calls.Values.Sum());
        output.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
        output.WriteLine($"{rowCount} rows, parse {parseTime.TotalMilliseconds:F2} ms, first layout {layoutTime.TotalMilliseconds:F2} ms, 50 cached layouts with culling {redrawTime.TotalMilliseconds:F2} ms");
        output.WriteLine($"Text measurement calls: {firstMeasureCount} on first layout, {calls.Values.Sum() - firstMeasureCount} on repeated layouts");
    }

    private static HtmlDrawItem TextItem(HtmlLayoutResult result, string text) =>
        Assert.Single(result.Items, item => string.Equals(item.Text?.Trim(), text, StringComparison.Ordinal));

    private static Size Measure(string text, HtmlTextStyle style) =>
        new(new StringInfo(text).LengthInTextElements * (style.FontSize / 2), style.FontSize);
}
