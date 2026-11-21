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
    [InlineData("block")]
    [InlineData("flex; flex-direction: column")]
    [InlineData("grid")]
    public async Task DisclosureAndHiddenChangesUpdateFlowPaintAndHitTesting(string display)
    {
        List<HtmlScriptError> errors = [];
        using var page = HtmlPage.Parse($$"""
            <style>body { margin: 0; } details { display: {{display}}; }</style>
            <button id="hide">Hide</button><button id="show">Show</button>
            <div id="wrapper"><details id="panel">
                secret
                <div id="before" style="height: 40px">Before</div>
                <summary id="summary"><span id="label">Toggle</span><button id="nested" type="button">Button</button></summary>
                <summary id="other">Other</summary>
                <input id="field">
                <div id="fixed" style="position: fixed; top: 200px; height: 20px; width: 90px">Fixed</div>
            </details></div>
            <div id="after">After</div>
            <script>
                const panel = document.getElementById('panel');
                const wrapper = document.getElementById('wrapper');
                document.getElementById('hide').addEventListener('click', () => {
                    wrapper.hidden = 1;
                    if (wrapper.hidden && panel.open) panel.setAttribute('data-state', 'hidden and open');
                });
                document.getElementById('show').addEventListener('click', () => {
                    wrapper.hidden = false;
                    panel.open = 0;
                    if (!wrapper.hidden && !panel.open) panel.setAttribute('data-state', 'visible and closed');
                });
                let cancel = true;
                document.getElementById('summary').addEventListener('click', event => {
                    if (cancel) { event.preventDefault(); cancel = false; }
                });
            </script>
            """, new HtmlPageOptions { OnScriptError = errors.Add });
        var panel = page.Document.GetElementById("panel")!;
        var layout = new HtmlLayout();
        HtmlLayoutResult Current() => layout.Get(page.Document, page.Version, 400, Measure);
        var closed = Current();
        Assert.DoesNotContain(closed.Items, item => item.Element.Id is "before" or "other" or "field" or "fixed" || item.Text == "secret");
        var label = TextItem(closed, "Toggle");
        var hit = Assert.IsAssignableFrom<IElement>(closed.HitTest(label.Bounds.Left + 1, label.Bounds.Top + 1));
        Assert.Equal("label", hit.Id);

        await page.ActivateAsync(hit);
        Assert.Same(closed, Current());
        Assert.False(panel.HasAttribute("open"));
        await page.ActivateAsync(page.Document.GetElementById("nested")!);
        Assert.False(panel.HasAttribute("open"));
        await page.ActivateAsync(hit);
        var opened = Current();
        Assert.True(panel.HasAttribute("open"));
        Assert.Contains(opened.Items, item => item.Element.Id == "field" && item.ControlBounds is not null);
        Assert.Contains(opened.Items, item => item.Element.Id == "fixed" && item.Fixed);
        Assert.True(TextItem(opened, "After").Bounds.Top > TextItem(closed, "After").Bounds.Top);
        await page.ActivateAsync(page.Document.GetElementById("other")!);
        Assert.True(panel.HasAttribute("open"));

        await page.ActivateAsync(page.Document.GetElementById("hide")!);
        Assert.Equal("hidden and open", panel.GetAttribute("data-state"));
        Assert.DoesNotContain(Current().Items, item => ReferenceEquals(item.Element, panel) || panel.Contains(item.Element));
        await page.ActivateAsync(page.Document.GetElementById("show")!);
        Assert.Equal("visible and closed", panel.GetAttribute("data-state"));
        Assert.Equal(label.Bounds, TextItem(Current(), "Toggle").Bounds);
        Assert.Equal(closed.Height, Current().Height);
        Assert.Empty(errors);
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

    [Fact]
    public void LinkedSheetsCascadeInDomOrderAndReevaluateViewportQueries()
    {
        var page = HtmlPage.Parse("""
            <style>.target { color: red; }</style>
            <link id="external" rel="STYLEsheet" href="theme.css" media="screen">
            <style>.target { font-weight: normal; } @media print { .target { color: black !important; } }</style>
            <main class="target" style="color: green">Target</main>
            """);
        string? External(IElement link) => link.Id == "external" ? """
            .target { color: blue !important; font-weight: bold; }
            @media print, (max-width: 400px) and (orientation: portrait) {
                .target { color: purple !important; }
                @media (min-height: 500px) { .target { font-size: 24px; } }
            }
            """ : null;
        var layout = new HtmlLayout();
        HtmlDrawItem Target(double width, double height) => TextItem(layout.Get(page.Document, page.Version, width, Measure,
            viewportHeight: height, externalStyleSheet: External), "Target");

        var wide = Target(600, 700);
        Assert.Equal(Color.FromRgb(0, 0, 255), wide.TextStyle.Color);
        Assert.False(wide.TextStyle.Bold);
        Assert.Equal(16, wide.TextStyle.FontSize);
        var narrow = Target(400, 600);
        Assert.Equal(Color.FromRgb(128, 0, 128), narrow.TextStyle.Color);
        Assert.Equal(24, narrow.TextStyle.FontSize);
        Assert.Equal(Color.FromRgb(0, 0, 255), Target(400, 300).TextStyle.Color);
    }

    [Fact]
    public void CombinatorsAttributesAndLiveControlStatesMatch()
    {
        var page = HtmlPage.Parse("""
            <style>
              body { margin: 0; }
              fieldset > input[type='checkbox']:checked + label[data-word~='one'] { color: red; }
              fieldset input:disabled ~ label[data-word^='ONE' i][data-end$='tail'] { font-weight: bold; }
              fieldset:focus-within > label[data-language|='en'] { font-style: italic; }
              fieldset > label[data-other*=','] { font-size: 20px; }
              input:enabled:focus { width: 30px; }
              input:enabled:hover:active { height: 20px; }
            </style>
            <fieldset disabled><input id="check" type="CHECKBOX" checked>
              <label data-word="one two">Adjacent</label>
              <section><label data-word="one">Nested</label></section>
              <label data-word="one-two" data-end="detail" data-language="en-US" data-other="a,b">Sibling</label>
            </fieldset>
            <input id="focus" type="checkbox"><fieldset><legend><input id="legend"></legend></fieldset>
            """);
        var layout = new HtmlLayout();
        var focus = page.Document.GetElementById("focus");
        var first = layout.Get(page.Document, page.Version, 400, Measure);
        Assert.Equal(Color.FromRgb(255, 0, 0), TextItem(first, "Adjacent").TextStyle.Color);
        Assert.Equal(HtmlTextStyle.Default, TextItem(first, "Nested").TextStyle);
        Assert.True(TextItem(first, "Sibling").TextStyle.Bold);
        Assert.Equal(20, TextItem(first, "Sibling").TextStyle.FontSize);
        Assert.False(TextItem(first, "Sibling").TextStyle.Italic);
        Assert.Equal(18, Box(first, "focus").ControlBounds!.Value.Width);

        var focused = layout.Get(page.Document, page.Version, 400, Measure,
            focusedElement: focus, hoveredElement: focus, activeElement: focus);
        Assert.Equal(30, Box(focused, "focus").ControlBounds!.Value.Width);
        Assert.Equal(20, Box(focused, "focus").ControlBounds!.Value.Height);
        var within = layout.Get(page.Document, page.Version, 400, Measure, focusedElement: page.Document.GetElementById("check"));
        Assert.True(TextItem(within, "Sibling").TextStyle.Italic);
    }

    [Fact]
    public void ImagesRespectIntrinsicAspectRatioCssAndAlternativeText()
    {
        var page = HtmlPage.Parse("""
            <style>body { margin: 0; } img { display: block; } #loaded { max-width: 50%; padding: 2px; border: 1px solid; }</style>
            <img id="loaded" alt="Image" width="160"><img id="missing" alt="Fallback" width="100" height="24">
            <img id="empty" alt="" width="80" height="10">
            """);
        static Size? Intrinsic(IElement img) => img.Id == "loaded" ? new(200, 100) : null;
        var result = new HtmlLayout().Get(page.Document, page.Version, 200, Measure, imageSize: Intrinsic);
        var loaded = Box(result, "loaded");
        Assert.Equal(new Rect(0, 0, 106, 56), loaded.Bounds);
        Assert.Equal(new Rect(3, 3, 100, 50), loaded.ImageBounds);
        Assert.DoesNotContain(result.Items, item => item.Text == "Image");
        var missing = Box(result, "missing");
        Assert.Equal(new Rect(0, 56, 100, 24), missing.Bounds);
        Assert.Null(missing.ImageBounds);
        Assert.Same(missing.Element, TextItem(result, "Fallback").Element);
        Assert.Equal(new Rect(0, 80, 80, 10), Box(result, "empty").Bounds);
        Assert.Same(loaded.Element, result.HitTest(102, 52));
    }

    [Fact]
    public void AutoMarginsCenterWidthConstrainedBlocks()
    {
        var page = HtmlPage.Parse("""
            <style>body { margin:0; } main { margin:0 auto; max-width:200px; height:20px; }</style>
            <main id="frame"></main>
            """);
        var layout = new HtmlLayout();
        Assert.Equal(new Rect(50, 0, 200, 20), Box(layout.Get(page.Document, page.Version, 300, Measure), "frame").Bounds);
        Assert.Equal(new Rect(0, 0, 180, 20), Box(layout.Get(page.Document, page.Version, 180, Measure), "frame").Bounds);
    }

    [Fact]
    public void FlexLaysOutGrowthGapsWrappingAndAlignment()
    {
        var page = HtmlPage.Parse("""
            <style>
              body { margin: 0; }
              #grow { display: flex; width: 200px; height: 50px; gap: 10px; align-items: center; }
              #one { flex: 1 0 40px; height: 20px; }
              #two { flex: 2 0 30px; height: 30px; }
              #wrap { display: flex; flex-flow: row wrap; width: 100px; gap: 5px; }
              #wrap > div { flex: 0 0 45px; height: 20px; }
            </style>
            <div id="grow"><div id="one"></div><div id="two"></div></div>
            <div id="wrap"><div id="a"></div><div id="b"></div><div id="c"></div></div>
            """);
        var result = new HtmlLayout().Get(page.Document, page.Version, 240, Measure);
        Assert.Equal(new Rect(0, 15, 80, 20), Box(result, "one").Bounds);
        Assert.Equal(new Rect(90, 10, 110, 30), Box(result, "two").Bounds);
        Assert.Equal(new Rect(0, 50, 45, 20), Box(result, "a").Bounds);
        Assert.Equal(new Rect(50, 50, 45, 20), Box(result, "b").Bounds);
        Assert.Equal(new Rect(0, 75, 45, 20), Box(result, "c").Bounds);
        Assert.Equal(95, result.Height);
    }

    [Fact]
    public void GridLaysOutFractionalTracksAutoRowsAndSpans()
    {
        var page = HtmlPage.Parse("""
            <style>
              body { margin: 0; }
              #grid { display:grid; width: 260px; grid-template-columns: 60px repeat(2, minmax(0, 1fr)); gap: 5px 10px; }
              #span { grid-column: 2 / span 2; grid-row: 1; height: 20px; }
              #long { grid-column: span 2; height: 30px; }
              #first { height: 10px; }
              #last { height: 15px; }
            </style>
            <section id="grid"><div id="first"></div><div id="span"></div><div id="long"></div><div id="last"></div></section>
            """);
        var result = new HtmlLayout().Get(page.Document, page.Version, 300, Measure);
        Assert.Equal(new Rect(0, 0, 60, 10), Box(result, "first").Bounds);
        Assert.Equal(new Rect(70, 0, 190, 20), Box(result, "span").Bounds);
        Assert.Equal(new Rect(0, 25, 160, 30), Box(result, "long").Bounds);
        Assert.Equal(new Rect(170, 25, 90, 15), Box(result, "last").Bounds);
    }

    [Fact]
    public void PositionedBoxesUseContainingBlockAndFixedBoxesFollowViewport()
    {
        var page = HtmlPage.Parse("""
            <style>body { margin: 0; }</style>
            <div id="relative" style="position:relative; left:8px; top:5px; width:90px; height:60px; border:2px solid red">
              <div id="bottom" style="position:absolute; right:4px; bottom:6px; width:20px; height:10px"></div>
              <div id="high" style="position:absolute; z-index:4; inset: 2px auto auto 4px; width:20px; height:12px"></div>
              <div id="low" style="position:absolute; z-index:1; top:2px; left:4px; width:18px; height:10px"></div>
            </div>
            <div id="normal" style="height: 236px"></div>
            <div id="fixed" style="position:fixed; right:10px; bottom:10px; width:30px; height:20px"></div>
            """);
        var result = new HtmlLayout().Get(page.Document, page.Version, 120, Measure, viewportHeight: 90);
        Assert.Equal(new Rect(8, 5, 94, 64), Box(result, "relative").Bounds);
        Assert.Equal(new Rect(76, 51, 20, 10), Box(result, "bottom").Bounds);
        Assert.Equal(64, Box(result, "normal").Bounds.Y);
        Assert.Equal(300, result.Height);
        Assert.Same(Box(result, "high").Element, result.HitTest(15, 10));
        var fixedBox = Box(result, "fixed");
        Assert.True(fixedBox.Fixed);
        Assert.Equal(new Rect(80, 60, 30, 20), fixedBox.Bounds);
        Assert.Same(fixedBox.Element, result.HitTest(85, 265, scrollOffset: 200));
        Assert.Contains(fixedBox, result.GetVisibleItems(200, 290, scrollOffset: 200));
    }

    [Fact]
    public void OverflowShorthandCascadesAndNormalizesMixedAxes()
    {
        using var page = HtmlPage.Parse("""
            <style>
              html { overflow: hidden scroll; }
              #mixed { width: 40px; height: 30px; overflow: clip; overflow-y: scroll !important; }
              #clip { width: 40px; height: 30px; overflow: visible clip; }
              #alias { width: 40px; height: 30px; overflow: overlay; }
            </style>
            <div id="mixed" style="overflow-y: hidden; overflow: nonsense hidden"><div style="width: 90px; height: 80px"></div></div>
            <div id="clip"><div style="width: 90px; height: 80px"></div></div>
            <div id="alias"></div>
            """);
        var layout = new HtmlLayout().Get(page.Document, page.Version, 200, Measure, viewportHeight: 100);
        Assert.Equal((HtmlOverflow.Hidden, HtmlOverflow.Scroll), (layout.ViewportX, layout.ViewportY));

        var mixed = layout.RegionsByElement[page.Document.GetElementById("mixed")!];
        Assert.Equal((HtmlOverflow.Hidden, HtmlOverflow.Scroll), (mixed.X, mixed.Y));
        Assert.Equal((50d, 50d), (mixed.MaxX, mixed.MaxY));
        Assert.True(mixed.BarY);
        Assert.False(mixed.UserX);
        Assert.False(mixed.BarX);
        var clip = layout.RegionsByElement[page.Document.GetElementById("clip")!];
        Assert.Equal((HtmlOverflow.Visible, HtmlOverflow.Clip), (clip.X, clip.Y));
        Assert.Equal((0d, 0d), (clip.MaxX, clip.MaxY));
        var alias = layout.RegionsByElement[page.Document.GetElementById("alias")!];
        Assert.Equal((HtmlOverflow.Auto, HtmlOverflow.Auto), (alias.X, alias.Y));
        Assert.False(alias.BarX || alias.BarY);
        Assert.True(HtmlStyles.SupportsDeclaration("OVERFLOW", " auto clip "));
        Assert.True(HtmlStyles.SupportsDeclaration("overflow-y", "overlay"));
        Assert.False(HtmlStyles.SupportsDeclaration("overflow", "visible auto scroll"));
        Assert.False(HtmlStyles.SupportsDeclaration("overflow-x", "auto clip"));
    }

    [Fact]
    public void NestedScrollOffsetsMovePaintHitAndNativeControlTogetherWhileFixedEscapes()
    {
        using var page = HtmlPage.Parse("""
            <style>body { margin: 0; }</style>
            <div id="outer" style="width:90px; height:70px; overflow:auto">
              <div id="inner" style="position:relative; width:110px; height:80px; margin:20px 0 0 25px; overflow:auto">
                <input id="field" style="position:absolute; left:140px; top:95px; width:40px; height:25px">
                <div id="fixed" style="position:fixed; left:5px; top:5px; width:10px; height:10px"></div>
              </div>
            </div>
            <section style="width:220px; height:250px"></section>
            """);
        var layout = new HtmlLayout().Get(page.Document, page.Version, 150, Measure, viewportHeight: 120);
        var outer = Assert.Single(layout.ScrollRegions, region => region.Element.Id == "outer");
        var inner = Assert.Single(layout.ScrollRegions, region => region.Element.Id == "inner");
        Assert.Same(outer, inner.Parent);
        Assert.Equal((45d, 30d), (outer.MaxX, outer.MaxY));
        Assert.Equal((70d, 40d), (inner.MaxX, inner.MaxY));
        Assert.Equal((220d, 320d), (layout.Width, layout.Height));

        var field = Box(layout, "field");
        var fixedBox = Box(layout, "fixed");
        Assert.True(fixedBox.Fixed);
        Assert.Null(fixedBox.ScrollParent);
        Assert.Same(inner, field.ScrollParent);
        var viewport = new Rect(0, 0, 150, 120);
        var before = new HtmlScrollFrame(layout, viewport, default);
        Assert.DoesNotContain(field, before.VisibleItems());
        Assert.NotEqual(field, before.HitTest(new(100, 90)));
        var frame = new HtmlScrollFrame(layout, viewport, new(5, 5), element => element.Id switch
        {
            "outer" => new(45, 30), "inner" => new(70, 40), _ => default
        });
        var (bounds, clip) = frame.Place(field, field.ControlBounds);
        Assert.Equal(new Rect(45, 40, 40, 25), bounds);
        Assert.Equal(new Rect(0, 0, 85, 65), clip);
        Assert.Contains(field, frame.VisibleItems());
        Assert.Equal(field, frame.HitTest(new(50, 50)));
        Assert.NotEqual(field, frame.HitTest(new(85, 64)));
        Assert.Equal(fixedBox.Bounds, frame.Place(fixedBox).Bounds);
        Assert.Equal(fixedBox, frame.HitTest(new(6, 6)));
    }

    [Fact]
    public void ElementScrollOffsetsSurviveMirrorReplacementAndClampToNewExtents()
    {
        using var browser = new HtmlBrowser();
        using var page = HtmlPage.CreateRendererMirror(browser, new Uri("https://example.test/"));
        var layout = new HtmlLayout();
        HtmlRendererNode html = new(1, 0, "html", null, null, null, null);
        HtmlRendererNode body = new(2, 1, "body", null, null, null, null);
        HtmlRendererNode panel = new(3, 2, "div", null,
            new() { ["id"] = "panel", ["style"] = "overflow: auto; width: 50px; height: 40px" }, null, null);
        HtmlRendererNode child = new(4, 3, "div", null, new() { ["style"] = "width: 150px; height: 140px" }, null, null);
        page.ApplyRendererSnapshot(new("", [html, body, panel, child]), 0);
        using var view = new HtmlView(page);
        var initial = layout.Get(page.Document, page.Version, 160, Measure);
        var before = Assert.Single(initial.ScrollRegions);
        Assert.True(view.ScrollElement(before, new(80, 75)));

        page.ApplyRendererSnapshot(new("", [html, body, panel, child with
        {
            Attributes = new() { ["style"] = "width: 110px; height: 90px" }
        }]), 0);
        var changed = layout.Get(page.Document, page.Version, 160, Measure);
        var after = Assert.Single(changed.ScrollRegions);
        Assert.NotSame(before.Element, after.Element);
        view.ReconcileElementOffsets(changed);
        Assert.Equal(new Point(60, 50), view.GetElementOffset(after.Element));

        page.ApplyRendererSnapshot(new("", [html, body]), 0);
        view.ReconcileElementOffsets(layout.Get(page.Document, page.Version, 160, Measure));
        page.ApplyRendererSnapshot(new("", [html, body, panel, child]), 0);
        Assert.Equal(default, view.GetElementOffset(page.Document.GetElementById("panel")!));
    }

    [Fact]
    public void ViewportScrollBarKeepsTheAssignedBoundsAndAcceptsHits()
    {
        using var page = HtmlPage.Parse("""
            <style>body { margin: 0; }</style><div style="width: 1600px; height: 1500px"></div>
            """);
        using var view = new HtmlView(page);
        var viewport = new Size(960, 680);
        view.Measure(viewport);
        view.Arrange(new Rect(10, 50, viewport.Width, viewport.Height));
        view.ArrangeViewport(viewport);
        var horizontal = Assert.IsType<Aprillz.MewUI.Controls.ScrollBar>(view[0]);
        var vertical = Assert.IsType<Aprillz.MewUI.Controls.ScrollBar>(view[1]);

        Assert.True(horizontal.IsVisible);
        Assert.Equal(new Rect(10, 716, 946, 14), horizontal.Bounds);
        Assert.Equal(946, horizontal.ViewportSize);
        Assert.Equal(654, horizontal.Maximum);
        Assert.Same(horizontal, horizontal.HitTest(new Point(40, 723)));
        Assert.True(vertical.IsVisible);
        Assert.Equal(new Rect(956, 50, 14, 666), vertical.Bounds);
        Assert.Equal(834, vertical.Maximum);
        Assert.Same(vertical, vertical.HitTest(new Point(963, 80)));
    }

    [Fact]
    public void NativeHostClipsHitTestingWithoutShrinkingTheControl()
    {
        using var child = new TestControl();
        using var host = new HtmlView.ClipHost(child);
        var fieldBounds = new Rect(30, 40, 80, 60);
        host.Place(fieldBounds, new Rect(50, 50, 30, 30));

        Assert.Equal(fieldBounds, child.Bounds);
        Assert.Equal(new Rect(50, 50, 30, 30), host.Bounds);
        Assert.Same(child, host.HitTest(new Point(60, 60)));
        Assert.Null(host.HitTest(new Point(40, 60)));
        Assert.Null(host.HitTest(new Point(90, 60)));
    }

    [Fact]
    public void ClippedContentDoesNotExpandDocumentAndVisibleAxisRemainsOpen()
    {
        using var page = HtmlPage.Parse("""
            <style>body { margin: 0; overflow: hidden auto; }</style>
            <div id="clip" style="overflow: visible clip; width: 50px; height: 30px">
              <div id="child" style="width: 250px; height: 200px"></div>
            </div>
            """);
        var layout = new HtmlLayout().Get(page.Document, page.Version, 100, Measure, viewportHeight: 80);
        Assert.Equal((HtmlOverflow.Hidden, HtmlOverflow.Auto), (layout.ViewportX, layout.ViewportY));
        Assert.Equal((250d, 30d), (layout.Width, layout.Height));
        var child = Box(layout, "child");
        Assert.Same(child.Element, layout.HitTest(75, 15));
        Assert.NotSame(child.Element, layout.HitTest(75, 45));
    }

    private sealed class TestControl : Aprillz.MewUI.Controls.Control
    {
        protected override Size MeasureOverride(Size availableSize) => availableSize;
        protected override Size ArrangeOverride(Size finalSize) => finalSize;
    }

    private static HtmlDrawItem Box(HtmlLayoutResult result, string id) =>
        Assert.Single(result.Items, item => item.Element.Id == id && item.Text is null);

    private static HtmlDrawItem TextItem(HtmlLayoutResult result, string text) =>
        Assert.Single(result.Items, item => string.Equals(item.Text?.Trim(), text, StringComparison.Ordinal));

    private static Size Measure(string text, HtmlTextStyle style) =>
        new(new StringInfo(text).LengthInTextElements * (style.FontSize / 2), style.FontSize);
}
