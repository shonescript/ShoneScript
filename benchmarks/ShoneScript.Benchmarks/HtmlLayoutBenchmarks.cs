// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Aprillz.MewUI;
using BenchmarkDotNet.Attributes;
using ShoneHtml;

namespace ShoneScript.Benchmarks;

[MemoryDiagnoser]
public class HtmlLayoutBenchmarks
{
    private const int CardCount = 48;
    private const int Wide = 960;
    private const int Narrow = 640;
    private const int Height = 720;
    private static readonly Func<string, HtmlTextStyle, Size> Measure =
        static (text, style) => new(text.Length * style.FontSize * 0.5, style.FontSize);
    private HtmlPage _page = null!;
    private HtmlLayout _layout = null!;
    private int _width;

    [GlobalSetup]
    public void Setup()
    {
        const string styles = """
            <style>
              body { margin: 0; font-size: 16px; }
              .cards { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; }
              .card { border: 1px solid gray; padding: 12px; }
              .card header { display: flex; justify-content: space-between; font-weight: bold; }
              .card p { line-height: 1.4; }
              @media (max-width: 700px) { .cards { grid-template-columns: repeat(2, 1fr); } }
            </style>
            """;
        string cards = string.Concat(Enumerable.Range(1, CardCount).Select(index => $"""
            <article class="card">
              <header><span>Card {index}</span><span>Ready</span></header>
              <p>This paragraph exercises text wrapping at different viewport widths.</p>
            </article>
            """));
        _page = HtmlPage.Parse($"{styles}<main class=\"cards\">{cards}</main>", new() { EnableScripts = false });
        _layout = new HtmlLayout();
        _width = Wide;
        var wide = Layout(_layout, Wide);
        var narrow = Layout(_layout, Narrow);
        if (wide.Items.Count(item => item.Text == $"Card {CardCount}") != 1 || narrow.Height <= wide.Height)
            throw new InvalidOperationException("The benchmark cards did not lay out as expected.");
        _ = Layout(_layout, Wide);
    }

    [GlobalCleanup]
    public void Cleanup() => _page.Dispose();

    [Benchmark]
    public int InitialLayout() => Layout(new HtmlLayout(), Wide).Items.Count;

    [Benchmark]
    public int ResizeLayout()
    {
        // Alternate widths to rebuild layout while keeping the style and measurement caches warm.
        _width = _width == Wide ? Narrow : Wide;
        return Layout(_layout, _width).Items.Count;
    }

    private HtmlLayoutResult Layout(HtmlLayout layout, int width) =>
        layout.Get(_page.Document, _page.Version, width, Measure, Height);
}
