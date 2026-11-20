// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using Aprillz.MewUI;

namespace ShoneHtml;

internal readonly record struct HtmlDrawItem(Rect Bounds, IElement Element, Color? Background,
    Color BorderColor, double BorderWidth, string? Text, HtmlTextStyle TextStyle);

internal sealed class HtmlLayoutResult
{
    private const int BatchSize = 16;
    private readonly HtmlDrawItem[] _items;
    private readonly VerticalRange[] _ranges;
    private readonly int _leaves;

    internal HtmlLayoutResult(double height, HtmlDrawItem[] items)
    {
        Height = height;
        _items = items;
        var batches = (items.Length + BatchSize - 1) / BatchSize;
        _leaves = 1;
        while (_leaves < batches)
            _leaves *= 2;
        _ranges = new VerticalRange[_leaves * 2];
        _ranges.AsSpan(_leaves).Fill(new(double.PositiveInfinity, double.NegativeInfinity));
        for (var i = 0; i < items.Length; i++)
        {
            var index = _leaves + i / BatchSize;
            var bounds = items[i].Bounds;
            _ranges[index] = _ranges[index].Include(new(bounds.Top, bounds.Bottom));
        }
        for (var i = _leaves - 1; i > 0; i--)
            _ranges[i] = _ranges[i * 2].Include(_ranges[i * 2 + 1]);
    }

    internal double Height { get; }
    internal IReadOnlyList<HtmlDrawItem> Items => _items;

    // Keep paint order while skipping batches outside the viewport.
    internal IEnumerable<HtmlDrawItem> GetVisibleItems(double top, double bottom)
    {
        if (!(top < bottom) || !MayOverlap(1, top, bottom))
            yield break;
        var pending = new Stack<int>();
        pending.Push(1);
        while (pending.TryPop(out var index))
        {
            if (index < _leaves)
            {
                if (MayOverlap(index * 2 + 1, top, bottom)) pending.Push(index * 2 + 1);
                if (MayOverlap(index * 2, top, bottom)) pending.Push(index * 2);
                continue;
            }
            var start = (index - _leaves) * BatchSize;
            var end = Math.Min(start + BatchSize, _items.Length);
            for (var i = start; i < end; i++)
            {
                var item = _items[i];
                if (item.Bounds.Bottom > top && item.Bounds.Top < bottom)
                    yield return item;
            }
        }
    }

    internal IElement? HitTest(double x, double y) => Hit(1, x, y);

    private IElement? Hit(int index, double x, double y)
    {
        if (!(_ranges[index].Top <= y && y < _ranges[index].Bottom))
            return null;
        if (index < _leaves)
            return Hit(index * 2 + 1, x, y) ?? Hit(index * 2, x, y);
        var start = (index - _leaves) * BatchSize;
        var end = Math.Min(start + BatchSize, _items.Length);
        for (var i = end - 1; i >= start; i--)
        {
            var item = _items[i];
            var bounds = item.Bounds;
            if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom)
                return item.Element;
        }
        return null;
    }

    private bool MayOverlap(int index, double top, double bottom) => _ranges[index].Top < bottom && _ranges[index].Bottom > top;

    private readonly record struct VerticalRange(double Top, double Bottom)
    {
        internal VerticalRange Include(VerticalRange other) => new(Math.Min(Top, other.Top), Math.Max(Bottom, other.Bottom));
    }
}

internal sealed class HtmlLayout
{
    private const int MeasurementLimit = 2048;
    private const int MeasurementTextLimit = 128;
    private readonly Dictionary<MeasureKey, Size> _measurements = new();
    private HtmlStyles _styles = new();
    private IDocument? _document;
    private long _version;
    private double _width;
    private Func<string, HtmlTextStyle, Size>? _measure;
    private HtmlLayoutResult? _result;

    internal HtmlLayoutResult Get(IDocument document, long version, double width, Func<string, HtmlTextStyle, Size> measure)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(measure);
        width = double.IsPositiveInfinity(width) ? 1_000_000 : Nonnegative(width);
        if (_result is not null && ReferenceEquals(_document, document) && _version == version && _width == width && _measure == measure)
            return _result;

        if (_measure != measure || !ReferenceEquals(_document, document))
            _measurements.Clear();
        _measure = measure;
        _result = null;
        if (!ReferenceEquals(_document, document) || _version != version)
            _styles.Update(document);
        _document = document;
        _version = version;
        _width = width;
        try
        {
            _result = new Builder(_styles, Measure).Build(document, width);
            return _result;
        }
        finally
        {
            _styles.EndLayout();
        }
    }

    internal void Clear()
    {
        _styles = new();
        _document = null;
        _measure = null;
        _result = null;
        _measurements.Clear();
    }

    private Size Measure(string text, HtmlTextStyle style)
    {
        var cache = text.Length <= MeasurementTextLimit;
        var key = new MeasureKey(text, style.FontFamily, style.FontSize, style.Bold, style.Italic);
        if (cache && _measurements.TryGetValue(key, out var size))
            return size;
        var measured = _measure!(text, style);
        size = new(Nonnegative(measured.Width), Nonnegative(measured.Height));
        if (cache)
        {
            if (_measurements.Count >= MeasurementLimit)
                _measurements.Clear();
            _measurements.Add(key, size);
        }
        return size;
    }

    private static double Nonnegative(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1_000_000) : 0;

    private readonly record struct MeasureKey(string Text, string? Family, double Size, bool Bold, bool Italic);

    private sealed class Builder(HtmlStyles styles, Func<string, HtmlTextStyle, Size> measure)
    {
        private double _rootFont = HtmlTextStyle.Default.FontSize;

        internal HtmlLayoutResult Build(IDocument document, double width)
        {
            if (document.DocumentElement is not { } root)
                return new(0, []);

            var plane = new Plane();
            var defaults = new HtmlStyle(HtmlTextStyle.Default);
            var viewport = new Flow(this, plane, 0, 0, width, null, defaults);
            // Keep one frame per open DOM element. Sibling runs do not grow the stack.
            var frames = new Stack<Frame>();
            if (Enter(root, defaults, viewport, out var rootFrame, isRoot: true))
                frames.Push(rootFrame);
            while (frames.TryPop(out var frame))
            {
                if (frame.Next is not { } child)
                {
                    Leave(frame);
                    continue;
                }
                frames.Push(frame with { Next = child.NextSibling });
                if (child is IText text)
                    frame.Content.AddText(text.Data, frame.Element, frame.Style);
                else if (child is IElement element && Enter(element, frame.Style, frame.Content, out var childFrame))
                    frames.Push(childFrame);
            }
            viewport.FlushLine();

            // Flatten paint order and resolve offsets once per nested inline-block.
            var height = Math.Max(0, viewport.Y);
            List<HtmlDrawItem> result = [];
            var pending = new Stack<PaintFrame>();
            pending.Push(new(plane, 0, 0, 0));
            while (pending.TryPop(out var current))
            {
                for (var i = current.Next; i < current.Plane.Items.Count; i++)
                {
                    var entry = current.Plane.Items[i];
                    if (entry.Nested is { } nested)
                    {
                        pending.Push(current with { Next = i + 1 });
                        pending.Push(new(nested, 0, current.X + nested.X, current.Y + nested.Y));
                        break;
                    }
                    var bounds = entry.Item.Bounds;
                    if (!(bounds.Width > 0 && bounds.Height > 0))
                        continue;
                    var moved = new Rect(bounds.X + current.X, bounds.Y + current.Y, bounds.Width, bounds.Height);
                    result.Add(entry.Item with { Bounds = moved });
                    height = Math.Max(height, moved.Bottom);
                }
            }
            return new(height, [.. result]);
        }

        private bool Enter(IElement element, in HtmlStyle parent, Flow flow, out Frame frame, bool isRoot = false)
        {
            frame = default;
            var style = styles.Compute(element, parent, _rootFont);
            if (isRoot)
                _rootFont = style.Text.FontSize;
            if (style.Display == HtmlDisplay.None)
                return false;
            if (element.LocalName == "br")
            {
                flow.BreakLine(style);
                return false;
            }

            var font = style.Text.FontSize;
            var margin = Resolve(style.Margin, flow.Width, font);
            var padding = Resolve(style.Padding, flow.Width, font);
            var border = style.HasBorder ? style.BorderWidth.Resolve(flow.Width, font, _rootFont) : 0;
            var insets = new Edges(padding.Top + border, padding.Right + border, padding.Bottom + border, padding.Left + border);
            if (style.Display == HtmlDisplay.Inline)
            {
                SpanBox? span = null;
                if (style.Background is not null || border > 0 || insets != default || margin.Left != 0 || margin.Right != 0)
                {
                    flow.PlacePendingSpace(insets.Left, CanWrap(style.WhiteSpace));
                    flow.Spacer(margin.Left);
                    span = flow.OpenSpan(element, style, border, insets);
                    flow.Spacer(insets.Left);
                }
                frame = new(element, element.FirstChild, style, flow, flow, -1, default, margin, insets, span);
                return true;
            }

            var isAtomic = style.Display == HtmlDisplay.InlineBlock;
            if (!isAtomic)
                flow.FlushLine();
            var contentPlane = isAtomic ? new Plane() : flow.Plane;
            var available = Math.Max(0, flow.Width - margin.Horizontal);
            var preferred = style.Width?.Resolve(flow.Width, font, _rootFont);
            var outerWidth = preferred is { } requested ? Math.Max(insets.Horizontal, requested + (style.BorderBox ? 0 : insets.Horizontal))
                : Math.Max(insets.Horizontal, available);
            var x = isAtomic ? 0 : flow.X + margin.Left;
            var y = isAtomic ? 0 : flow.Y + margin.Top;
            var explicitHeight = style.Height is { } height && (height.Unit != CssUnit.Percent || flow.ExplicitHeight is not null)
                ? Math.Max(0, height.Resolve(flow.ExplicitHeight ?? 0, font, _rootFont) - (style.BorderBox ? insets.Vertical : 0))
                : (double?)null;
            var item = AddBox(contentPlane, element, style, border);
            var content = new Flow(this, contentPlane, x + insets.Left, y + insets.Top,
                outerWidth - insets.Horizontal, explicitHeight, style);
            frame = new(element, element.FirstChild, style, flow, content, item, new(x, y, outerWidth, 0), margin, insets, null);
            return true;
        }

        private void Leave(in Frame frame)
        {
            var flow = frame.Flow;
            if (frame.Style.Display == HtmlDisplay.Inline)
            {
                if (frame.Span is { } span)
                {
                    flow.Spacer(frame.Insets.Right);
                    flow.CloseSpan(span);
                    flow.Spacer(frame.Margin.Right);
                }
                return;
            }

            var content = frame.Content;
            content.FlushLine();
            var outerHeight = (content.ExplicitHeight ?? Math.Max(0, content.Y - content.Top)) + frame.Insets.Vertical;
            var isAtomic = frame.Style.Display == HtmlDisplay.InlineBlock;
            var width = isAtomic && frame.Style.Width is null
                ? Math.Min(frame.Box.Width, content.MaxUsedWidth + frame.Insets.Horizontal)
                : frame.Box.Width;
            SetBounds(content.Plane, frame.Item, new(frame.Box.X, frame.Box.Y, width, outerHeight));
            if (isAtomic)
            {
                var totalWidth = width + frame.Margin.Horizontal;
                var wrap = CanWrap(frame.Style.WhiteSpace);
                flow.PlacePendingSpace(totalWidth, wrap);
                if (wrap && flow.HasLineContent && flow.Cursor + totalWidth > flow.Width)
                    flow.FlushLine();
                flow.PlaceAtomic(content.Plane, frame.Margin, totalWidth, outerHeight);
            }
            else
            {
                flow.Y = frame.Box.Y + outerHeight + frame.Margin.Bottom;
                flow.MaxUsedWidth = Math.Max(flow.MaxUsedWidth, Math.Max(width + frame.Margin.Horizontal,
                    content.MaxUsedWidth + frame.Insets.Horizontal + frame.Margin.Left));
            }
        }

        private Edges Resolve(CssEdges edges, double width, double font) => new(edges.Top.Resolve(width, font, _rootFont),
            edges.Right.Resolve(width, font, _rootFont), edges.Bottom.Resolve(width, font, _rootFont), edges.Left.Resolve(width, font, _rootFont));

        private static int AddBox(Plane plane, IElement element, in HtmlStyle style, double border)
        {
            var index = plane.Items.Count;
            plane.Items.Add(new(new(default, element, style.Background, style.BorderColor ?? style.Text.Color, border, null, style.Text)));
            return index;
        }

        private static void SetBounds(Plane plane, int index, Rect bounds)
        {
            var entry = plane.Items[index];
            plane.Items[index] = entry with { Item = entry.Item with { Bounds = bounds } };
        }

        private readonly record struct PaintEntry(HtmlDrawItem Item, Plane? Nested = null);
        private readonly record struct PaintFrame(Plane Plane, int Next, double X, double Y);
        private readonly record struct Edges(double Top, double Right, double Bottom, double Left)
        {
            internal double Horizontal => Left + Right;
            internal double Vertical => Top + Bottom;
        }

        private readonly record struct Frame(IElement Element, INode? Next, HtmlStyle Style, Flow Flow, Flow Content,
            int Item, Rect Box, Edges Margin, Edges Insets, SpanBox? Span);

        private sealed class Plane
        {
            internal readonly List<PaintEntry> Items = [];
            internal double X;
            internal double Y;
        }

        private sealed class SpanBox(IElement element, HtmlStyle style, double border, Edges insets)
        {
            internal readonly IElement Element = element;
            internal readonly HtmlStyle Style = style;
            internal readonly double Border = border;
            internal readonly Edges Insets = insets;
            internal int Index = -1;
            internal double Start;
            internal double OuterTop;
            internal double OuterBottom;
        }

        private static bool CanWrap(HtmlWhiteSpace whiteSpace) => whiteSpace is HtmlWhiteSpace.Normal or HtmlWhiteSpace.PreWrap;

        private sealed class Flow(Builder owner, Plane plane, double x, double y, double width, double? explicitHeight, HtmlStyle style)
        {
            private List<SpanBox>? _spans;
            private (IElement Element, HtmlStyle Style)? _space;
            private readonly StringBuilder _text = new();
            private IElement? _textElement;
            private HtmlTextStyle _textStyle;
            private double _textStart;
            private double _textEnd;
            private double _textHeight;
            private double _lineHeight;
            private bool _needSpanContinuations;

            internal double X { get; } = x;
            internal double Top { get; } = y;
            internal double Width { get; } = width;
            internal double? ExplicitHeight { get; } = explicitHeight;
            internal Plane Plane { get; } = plane;
            internal double Y = y;
            internal double Cursor;
            internal double MaxUsedWidth;
            internal bool HasLineContent { get; private set; }

            internal void AddText(string text, IElement element, in HtmlStyle textStyle)
            {
                var preserve = textStyle.WhiteSpace is HtmlWhiteSpace.Pre or HtmlWhiteSpace.PreWrap;
                var wrap = CanWrap(textStyle.WhiteSpace);
                var offset = 0;
                while (offset < text.Length)
                {
                    var current = text[offset];
                    if (IsSpace(current))
                    {
                        offset++;
                        if (!preserve)
                        {
                            _space ??= (element, textStyle);
                            continue;
                        }
                        if (current is '\r' or '\n')
                        {
                            if (current == '\r' && offset < text.Length && text[offset] == '\n') offset++;
                            BreakLine(textStyle);
                        }
                        else
                            AddToken(current == '\t' ? "    " : " ", element, textStyle, wrap);
                        continue;
                    }

                    var start = offset;
                    // East Asian text can wrap between graphemes even when it has no spaces.
                    if (wrap && IsEastAsian(text.AsSpan(offset)))
                        offset += StringInfo.GetNextTextElementLength(text.AsSpan(offset));
                    else
                    {
                        do { offset++; }
                        while (offset < text.Length && !IsSpace(text[offset]) && (!wrap || !IsEastAsian(text.AsSpan(offset))));
                    }
                    AddToken(text.AsSpan(start, offset - start), element, textStyle, wrap);
                }
            }

            private void AddToken(ReadOnlySpan<char> token, IElement element, in HtmlStyle tokenStyle, bool wrap)
            {
                if (!wrap || token.Length <= 512)
                {
                    var value = token.ToString();
                    var size = owner.MeasureText(value, tokenStyle.Text);
                    PlacePendingSpace(size.Width, wrap);
                    if (wrap && HasLineContent && Cursor + size.Width > Width)
                        FlushLine();
                    if (!wrap || size.Width <= Width || token.Length == 1)
                    {
                        AddTextPart(value, size, element, tokenStyle);
                        return;
                    }
                }
                else
                {
                    // Long unbroken text is measured in bounded probes as it flows across lines.
                    _space = null;
                    if (HasLineContent)
                        FlushLine();
                }
                AddWrappedToken(token, element, tokenStyle);
            }

            private void AddWrappedToken(ReadOnlySpan<char> token, IElement element, in HtmlStyle tokenStyle)
            {
                while (!token.IsEmpty)
                {
                    var available = Math.Max(0, Width - Cursor);
                    var length = StringInfo.GetNextTextElementLength(token);
                    var best = token[..length].ToString();
                    var bestSize = owner.MeasureText(best, tokenStyle.Text);
                    // Grow the prefix exponentially, then search the grapheme boundaries of the final probe.
                    if (bestSize.Width <= available && length < token.Length)
                    {
                        var ends = new List<int> { length };
                        var fitted = 0;
                        while (ends[^1] < token.Length)
                        {
                            var goal = ends.Count <= int.MaxValue / 2 ? ends.Count * 2 : int.MaxValue;
                            while (ends.Count < goal && ends[^1] < token.Length)
                            {
                                var last = ends[^1];
                                ends.Add(last + StringInfo.GetNextTextElementLength(token[last..]));
                            }
                            var probe = token[..ends[^1]].ToString();
                            var probeSize = owner.MeasureText(probe, tokenStyle.Text);
                            if (probeSize.Width <= available)
                            {
                                fitted = ends.Count - 1;
                                best = probe;
                                bestSize = probeSize;
                                continue;
                            }
                            var low = fitted + 1;
                            var high = ends.Count - 2;
                            while (low <= high)
                            {
                                var mid = low + (high - low) / 2;
                                probe = token[..ends[mid]].ToString();
                                probeSize = owner.MeasureText(probe, tokenStyle.Text);
                                if (probeSize.Width <= available)
                                {
                                    best = probe;
                                    bestSize = probeSize;
                                    low = mid + 1;
                                }
                                else
                                    high = mid - 1;
                            }
                            break;
                        }
                    }
                    AddTextPart(best, bestSize, element, tokenStyle);
                    token = token[best.Length..];
                    if (!token.IsEmpty)
                        FlushLine();
                }
            }

            internal void PlacePendingSpace(double nextWidth, bool wrap = true)
            {
                if (_space is not { } space)
                    return;
                _space = null;
                if (!HasLineContent)
                    return;
                var size = owner.MeasureText(" ", space.Style.Text);
                if (wrap && Cursor + size.Width + nextWidth > Width)
                    FlushLine();
                else
                    AddTextPart(" ", size, space.Element, space.Style);
            }

            private void AddTextPart(string text, Size size, IElement element, in HtmlStyle tokenStyle)
            {
                BeginLine();
                if (!ReferenceEquals(_textElement, element) || _textStyle != tokenStyle.Text || _textEnd != Cursor)
                {
                    FlushText();
                    _textElement = element;
                    _textStyle = tokenStyle.Text;
                    _textStart = Cursor;
                    _textHeight = 0;
                }
                _text.Append(text);
                _textEnd = Cursor + size.Width;
                _textHeight = Math.Max(_textHeight, size.Height > 0 ? size.Height : tokenStyle.Text.FontSize);
                var lineHeight = owner.LineHeight(tokenStyle, _textHeight);
                Advance(size.Width, lineHeight);
            }

            internal void FlushText()
            {
                if (_textElement is not { } element)
                    return;
                var bounds = new Rect(X + _textStart, Y + SpanTop, Math.Max(0, _textEnd - _textStart), _textHeight);
                Plane.Items.Add(new(new(bounds, element, null, default, 0, _text.ToString(), _textStyle)));
                _textElement = null;
                _text.Clear();
            }

            private double SpanTop { get; set; }
            private double SpanBottom { get; set; }

            internal void PlaceAtomic(Plane nested, Edges margin, double width, double height)
            {
                BeginLine();
                FlushText();
                nested.X = X + Cursor + margin.Left;
                nested.Y = Y + margin.Top + SpanTop;
                Plane.Items.Add(new(default, nested));
                Advance(width, height + margin.Vertical);
            }

            private void Advance(double width, double height)
            {
                Cursor += width;
                MaxUsedWidth = Math.Max(MaxUsedWidth, Cursor);
                _lineHeight = Math.Max(_lineHeight, height + SpanTop + SpanBottom);
                HasLineContent = true;
            }

            internal void Spacer(double width)
            {
                if (width == 0)
                    return;
                FlushText();
                BeginLine();
                Advance(width, owner.LineHeight(style, style.Text.FontSize));
            }

            internal SpanBox OpenSpan(IElement element, in HtmlStyle inlineStyle, double border, Edges insets)
            {
                FlushText();
                BeginLine();
                var span = new SpanBox(element, inlineStyle, border, insets);
                (_spans ??= []).Add(span);
                SpanTop += insets.Top;
                SpanBottom += insets.Bottom;
                StartSpan(span, SpanTop - insets.Top, SpanBottom - insets.Bottom);
                return span;
            }

            internal void CloseSpan(SpanBox span)
            {
                FlushText();
                if (span.Index >= 0)
                {
                    if (Cursor > span.Start)
                        _lineHeight = Math.Max(_lineHeight, owner.LineHeight(span.Style, span.Style.Text.FontSize) + SpanTop + SpanBottom);
                    FinishSpan(span, _lineHeight);
                }
                _spans!.RemoveAt(_spans.Count - 1);
                SpanTop -= span.Insets.Top;
                SpanBottom -= span.Insets.Bottom;
            }

            private void BeginLine()
            {
                if (!_needSpanContinuations)
                    return;
                _needSpanContinuations = false;
                var top = 0d;
                var bottom = 0d;
                foreach (var span in _spans!)
                {
                    StartSpan(span, top, bottom);
                    top += span.Insets.Top;
                    bottom += span.Insets.Bottom;
                }
            }

            private void StartSpan(SpanBox span, double outerTop, double outerBottom)
            {
                span.Index = AddBox(Plane, span.Element, span.Style, span.Border);
                span.Start = Cursor;
                span.OuterTop = outerTop;
                span.OuterBottom = outerBottom;
            }

            private void FinishSpan(SpanBox span, double height)
            {
                SetBounds(Plane, span.Index, new(X + span.Start, Y + span.OuterTop, Math.Max(0, Cursor - span.Start),
                    Math.Max(0, height - span.OuterTop - span.OuterBottom)));
                span.Index = -1;
            }

            internal void BreakLine(in HtmlStyle breakStyle)
            {
                BeginLine();
                _lineHeight = Math.Max(_lineHeight, owner.LineHeight(breakStyle, breakStyle.Text.FontSize) + SpanTop + SpanBottom);
                FlushLine();
            }

            internal void FlushLine()
            {
                FlushText();
                if (_spans is { Count: > 0 } && !_needSpanContinuations)
                {
                    foreach (var span in _spans)
                        FinishSpan(span, _lineHeight);
                    _needSpanContinuations = true;
                }
                Y += _lineHeight;
                Cursor = 0;
                _lineHeight = 0;
                _space = null;
                HasLineContent = false;
            }

            private static bool IsSpace(char value) => value is ' ' or '\t' or '\r' or '\n' or '\f';

            private static bool IsEastAsian(ReadOnlySpan<char> text)
            {
                if (text[0] is >= '\u2e80' and <= '\u9fff' or >= '\uac00' and <= '\ud7af' or >= '\uf900' and <= '\ufaff' or >= '\uff01' and <= '\uff60')
                    return true;
                return text.Length > 1 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]) && char.ConvertToUtf32(text[0], text[1]) is >= 0x20000 and <= 0x323AF;
            }
        }

        private Size MeasureText(string text, HtmlTextStyle style) => measure(text, style);

        private double LineHeight(in HtmlStyle style, double measured) => style.LineHeight is { } specified
            ? specified.Resolve(style.Text.FontSize, style.Text.FontSize, _rootFont)
            : Math.Max(style.Text.FontSize * 1.2, measured);
    }
}
