// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Aprillz.MewUI;

namespace ShoneHtml;

internal readonly record struct HtmlDrawItem(Rect Bounds, IElement Element, Color? Background,
    Color BorderColor, double BorderWidth, string? Text, HtmlTextStyle TextStyle,
    Rect? ImageBounds = null, Rect? ControlBounds = null, bool Fixed = false);

internal sealed class HtmlLayoutResult
{
    private const int BatchSize = 16;
    private readonly HtmlDrawItem[] _items;
    private readonly int _flowCount;
    private readonly VerticalRange[] _ranges;
    private readonly int _leaves;

    internal HtmlLayoutResult(double height, HtmlDrawItem[] items)
    {
        Height = height;
        _items = items;
        _flowCount = Array.FindIndex(items, static item => item.Fixed);
        if (_flowCount < 0) _flowCount = items.Length;
        var batches = (_flowCount + BatchSize - 1) / BatchSize;
        _leaves = 1;
        while (_leaves < batches)
            _leaves *= 2;
        _ranges = new VerticalRange[_leaves * 2];
        _ranges.AsSpan(_leaves).Fill(new(double.PositiveInfinity, double.NegativeInfinity));
        for (var i = 0; i < _flowCount; i++)
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
    internal IEnumerable<HtmlDrawItem> GetVisibleItems(double top, double bottom, double scrollOffset = 0)
    {
        if (!(top < bottom)) yield break;
        foreach (var item in GetVisibleFlowItems(top, bottom)) yield return item;
        for (var i = _flowCount; i < _items.Length; i++)
            if (_items[i].Bounds.Bottom > top - scrollOffset && _items[i].Bounds.Top < bottom - scrollOffset)
                yield return _items[i];
    }

    private IEnumerable<HtmlDrawItem> GetVisibleFlowItems(double top, double bottom)
    {
        if (!MayOverlap(1, top, bottom))
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
            var end = Math.Min(start + BatchSize, _flowCount);
            for (var i = start; i < end; i++)
            {
                var item = _items[i];
                if (item.Bounds.Bottom > top && item.Bounds.Top < bottom)
                    yield return item;
            }
        }
    }

    internal IElement? HitTest(double x, double y, double scrollOffset = 0)
    {
        for (var i = _items.Length - 1; i >= _flowCount; i--)
            if (Contains(_items[i].Bounds, x, y - scrollOffset)) return _items[i].Element;
        return Hit(1, x, y);
    }

    private static bool Contains(Rect bounds, double x, double y) => x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;

    private IElement? Hit(int index, double x, double y)
    {
        if (!(_ranges[index].Top <= y && y < _ranges[index].Bottom))
            return null;
        if (index < _leaves)
            return Hit(index * 2 + 1, x, y) ?? Hit(index * 2, x, y);
        var start = (index - _leaves) * BatchSize;
        var end = Math.Min(start + BatchSize, _flowCount);
        for (var i = end - 1; i >= start; i--)
        {
            var item = _items[i];
            if (Contains(item.Bounds, x, y)) return item.Element;
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
    private double _width, _height;
    private Func<string, HtmlTextStyle, Size>? _measure;
    private Func<IElement, string?>? _externalStyleSheet;
    private Func<IElement, Size?>? _imageSize;
    private IElement? _focused, _hovered, _active;
    private HtmlLayoutResult? _result;

    internal HtmlLayoutResult Get(IDocument document, long version, double width, Func<string, HtmlTextStyle, Size> measure,
        double viewportHeight = 0, Func<IElement, string?>? externalStyleSheet = null, Func<IElement, Size?>? imageSize = null,
        IElement? focusedElement = null, IElement? hoveredElement = null, IElement? activeElement = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(measure);
        width = double.IsPositiveInfinity(width) ? 1_000_000 : Nonnegative(width);
        viewportHeight = Nonnegative(viewportHeight);
        if (_result is not null && ReferenceEquals(_document, document) && _version == version && _width == width &&
            _height == viewportHeight && _measure == measure && _externalStyleSheet == externalStyleSheet && _imageSize == imageSize &&
            ReferenceEquals(_focused, focusedElement) && ReferenceEquals(_hovered, hoveredElement) && ReferenceEquals(_active, activeElement))
            return _result;

        if (_measure != measure || !ReferenceEquals(_document, document))
            _measurements.Clear();
        _measure = measure;
        _result = null;
        if (!ReferenceEquals(_document, document) || _version != version || _externalStyleSheet != externalStyleSheet)
            _styles.Update(document, externalStyleSheet);
        _document = document;
        _version = version;
        _width = width;
        _height = viewportHeight;
        _externalStyleSheet = externalStyleSheet;
        _imageSize = imageSize;
        (_focused, _hovered, _active) = (focusedElement, hoveredElement, activeElement);
        _styles.BeginLayout(width, viewportHeight, focusedElement, hoveredElement, activeElement);
        try
        {
            _result = new Builder(_styles, Measure, imageSize).Build(document, width, viewportHeight);
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
        _externalStyleSheet = null;
        _imageSize = null;
        _focused = _hovered = _active = null;
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

    private sealed class Builder(HtmlStyles styles, Func<string, HtmlTextStyle, Size> measure, Func<IElement, Size?>? imageSize)
    {
        private double _rootFont = HtmlTextStyle.Default.FontSize;
        private PositionContext _fixed = null!;
        private int _specialDepth;
        private bool _probing;

        internal HtmlLayoutResult Build(IDocument document, double width, double viewportHeight)
        {
            if (document.DocumentElement is not { } root)
                return new(0, []);

            var plane = new Plane();
            var initial = new PositionContext(plane);
            _fixed = new(new Plane());
            var defaults = new HtmlStyle(HtmlTextStyle.Default);
            var viewport = new Flow(this, plane, 0, 0, width, null, defaults, initial);
            Walk(root, defaults, viewport, isRoot: true);
            viewport.FlushLine();
            var viewportBox = new Rect(0, 0, width, viewportHeight);
            PlacePositioned(initial, viewportBox);
            PlacePositioned(_fixed, viewportBox);
            List<HtmlDrawItem> result = [];
            var height = Flatten(plane, result, Math.Max(0, viewport.Y), false);
            Flatten(_fixed.Plane, result, 0, true);
            return new(height, [.. result]);
        }

        private void Walk(IElement root, in HtmlStyle parent, Flow flow, bool isRoot = false, HtmlStyle? rootStyle = null,
            double? forceWidth = null, double? forceHeight = null)
        {
            // Keep one frame per open DOM element. Sibling runs do not grow the stack.
            var frames = new Stack<Frame>();
            if (Enter(root, parent, flow, out var rootFrame, isRoot, rootStyle, forceWidth, forceHeight)) frames.Push(rootFrame);
            while (frames.TryPop(out var frame))
            {
                if (frame.Next is not { } child)
                {
                    Leave(frame);
                    continue;
                }
                frames.Push(frame with { Next = frame.OnlyChild ? null : child.NextSibling });
                if (child is IText text)
                    frame.Content.AddText(text.Data, frame.Element, frame.Style);
                else if (child is IElement element && Enter(element, frame.Style, frame.Content, out var childFrame))
                    frames.Push(childFrame);
            }
        }

        private static double Flatten(Plane plane, List<HtmlDrawItem> result, double height, bool fixedPosition)
        {
            var pending = new Stack<PaintFrame>();
            pending.Push(new(plane.PaintItems, 0, 0, 0));
            while (pending.TryPop(out var current))
            {
                for (var i = current.Next; i < current.Items.Count; i++)
                {
                    var entry = current.Items[i];
                    if (entry.Nested is { } nested)
                    {
                        pending.Push(current with { Next = i + 1 });
                        pending.Push(new(nested.PaintItems, 0, current.X + nested.X, current.Y + nested.Y));
                        break;
                    }
                    var bounds = entry.Item.Bounds;
                    if (!(bounds.Width > 0 && bounds.Height > 0))
                        continue;
                    var moved = bounds.Offset(current.X, current.Y);
                    result.Add(entry.Item with
                    {
                        Bounds = moved, Fixed = fixedPosition,
                        ImageBounds = entry.Item.ImageBounds?.Offset(current.X, current.Y),
                        ControlBounds = entry.Item.ControlBounds?.Offset(current.X, current.Y)
                    });
                    height = Math.Max(height, moved.Bottom);
                }
            }
            return height;
        }

        private bool Enter(IElement element, in HtmlStyle parent, Flow flow, out Frame frame, bool isRoot = false,
            HtmlStyle? rootStyle = null, double? forceWidth = null, double? forceHeight = null)
        {
            frame = default;
            var style = rootStyle ?? styles.Compute(element, parent, _rootFont);
            if (isRoot)
                _rootFont = style.Text.FontSize;
            if (style.Display == HtmlDisplay.None) return false;
            if (style.Position is HtmlPosition.Absolute or HtmlPosition.Fixed && rootStyle is null)
            {
                QueuePositioned(element, style, flow);
                return false;
            }
            if (element.LocalName == "br")
            {
                flow.BreakLine(style);
                return false;
            }

            var closed = IsClosedDisclosure(element);
            var first = closed ? HtmlPage.FirstSummary(element) : element.FirstChild;
            var font = style.Text.FontSize;
            var margin = Resolve(style.Margin, flow.Width, font);
            var padding = Resolve(style.Padding, flow.Width, font);
            var border = style.HasBorder ? style.BorderWidth.Resolve(flow.Width, font, _rootFont) : 0;
            var insets = new Edges(padding.Top + border, padding.Right + border, padding.Bottom + border, padding.Left + border);
            var replacement = GetReplacement(element, style);
            if (style.Display == HtmlDisplay.Inline && replacement is null && style.Position == HtmlPosition.Static)
            {
                SpanBox? span = null;
                if (style.Background is not null || border > 0 || insets != default || margin.Left != 0 || margin.Right != 0)
                {
                    flow.PlacePendingSpace(insets.Left, CanWrap(style.WhiteSpace));
                    flow.Spacer(margin.Left);
                    span = flow.OpenSpan(element, style, border, insets);
                    flow.Spacer(insets.Left);
                }
                frame = new(element, first, style, flow, flow, -1, default, margin, insets, span, OnlyChild: closed);
                return true;
            }

            var isAtomic = style.Display is HtmlDisplay.Inline or HtmlDisplay.InlineBlock or HtmlDisplay.InlineFlex or HtmlDisplay.InlineGrid;
            var isolated = isAtomic || style.Position != HtmlPosition.Static;
            if (!isAtomic) flow.FlushLine();
            var contentPlane = isolated ? new Plane() : flow.Plane;
            var available = Math.Max(0, flow.Width - margin.Horizontal);
            var preferred = ContentSize(style.Width, flow.Width, font, style.BorderBox, insets.Horizontal);
            var explicitHeight = ContentSize(style.Height, flow.ExplicitHeight, font, style.BorderBox, insets.Vertical);
            var preferredHeight = explicitHeight;
            if (replacement is { } replaced)
            {
                preferred ??= replaced.AttributeWidth;
                preferredHeight ??= replaced.AttributeHeight;
                var ratio = replaced.Alt is null && replaced.Intrinsic is { Width: > 0, Height: > 0 } natural && !replaced.Control
                    ? natural.Width / natural.Height : (double?)null;
                var widthFromIntrinsic = preferred is null;
                var heightFromIntrinsic = preferredHeight is null;
                preferred ??= ratio is { } r && preferredHeight is { } h ? h * r : replaced.Intrinsic?.Width ?? 0;
                preferredHeight ??= ratio is { } aspect ? preferred.Value / aspect : replaced.Intrinsic?.Height ?? 0;
                var limitedWidth = Limit(preferred.Value, style.MinWidth, style.MaxWidth, flow.Width, font, style.BorderBox, insets.Horizontal);
                var limitedHeight = Limit(preferredHeight.Value, style.MinHeight, style.MaxHeight, flow.ExplicitHeight, font, style.BorderBox, insets.Vertical);
                if (ratio is { } imageRatio)
                {
                    if (heightFromIntrinsic && forceWidth is { } layoutWidth)
                        limitedHeight = Limit((layoutWidth - insets.Horizontal) / imageRatio, style.MinHeight, style.MaxHeight, flow.ExplicitHeight, font, style.BorderBox, insets.Vertical);
                    else if (heightFromIntrinsic && limitedWidth != preferred.Value)
                        limitedHeight = Limit(limitedWidth / imageRatio, style.MinHeight, style.MaxHeight, flow.ExplicitHeight, font, style.BorderBox, insets.Vertical);
                    else if (widthFromIntrinsic && limitedHeight != preferredHeight.Value)
                        limitedWidth = Limit(limitedHeight * imageRatio, style.MinWidth, style.MaxWidth, flow.Width, font, style.BorderBox, insets.Horizontal);
                }
                preferred = limitedWidth;
                preferredHeight = limitedHeight;
            }
            var outerWidth = forceWidth is { } forced ? Math.Max(insets.Horizontal, forced) : insets.Horizontal +
                Limit(preferred ?? Math.Max(0, available - insets.Horizontal), style.MinWidth, style.MaxWidth, flow.Width, font, style.BorderBox, insets.Horizontal);
            if (!isAtomic && rootStyle is null && available > outerWidth)
            {
                var autoLeft = style.Margin.Left.Unit == CssUnit.Auto;
                var autoRight = style.Margin.Right.Unit == CssUnit.Auto;
                var slack = (available - outerWidth) / (autoLeft && autoRight ? 2 : 1);
                margin = margin with { Left = autoLeft ? slack : margin.Left, Right = autoRight ? slack : margin.Right };
            }
            explicitHeight = forceHeight is { } forcedHeight ? Math.Max(0, forcedHeight - insets.Vertical) : preferredHeight;
            var x = isolated ? 0 : flow.X + margin.Left;
            var y = isolated ? 0 : flow.Y + margin.Top;
            var position = style.Position == HtmlPosition.Static ? flow.Position : new PositionContext(contentPlane);
            var item = AddBox(contentPlane, element, style, border);
            var content = new Flow(this, contentPlane, x + insets.Left, y + insets.Top,
                outerWidth - insets.Horizontal, explicitHeight, style, position);
            frame = new(element, first, style, flow, content, item, new(x, y, outerWidth, 0), margin, insets, null,
                isolated, isAtomic, border, forceWidth is null && preferred is null, OnlyChild: closed);
            if (replacement is { } substitute)
            {
                var surface = new Rect(content.X, content.Top, content.Width, explicitHeight ?? 0);
                var paint = contentPlane.Items[item];
                contentPlane.Items[item] = paint with { Item = paint.Item with
                {
                    ImageBounds = !substitute.Control && substitute.Alt is null && substitute.Intrinsic is not null ? surface : null,
                    ControlBounds = substitute.Control ? surface : null
                } };
                if (substitute.Alt is { Length: > 0 } alt && surface.Width > 0 && surface.Height > 0)
                {
                    var measured = MeasureText(alt, style.Text);
                    var textBox = new Rect(surface.X, surface.Y, Math.Min(surface.Width, measured.Width), Math.Min(surface.Height, measured.Height));
                    contentPlane.Items.Add(new(new(textBox, element, null, default, 0, alt, style.Text)));
                }
                Leave(frame);
                return false;
            }
            if (IsContainer(style.Display) && _specialDepth < 96 && !_probing)
            {
                _specialDepth++;
                try
                {
                    if (style.Display is HtmlDisplay.Flex or HtmlDisplay.InlineFlex) LayoutFlex(element, style, content);
                    else LayoutGrid(element, style, content);
                }
                finally { _specialDepth--; }
                Leave(frame);
                return false;
            }
            return true;
        }

        private void Leave(in Frame frame)
        {
            var flow = frame.Flow;
            if (frame.Item < 0)
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
            var style = frame.Style;
            var font = style.Text.FontSize;
            var outerHeight = Limit(content.ExplicitHeight ?? Math.Max(0, content.Y - content.Top), style.MinHeight, style.MaxHeight,
                flow.ExplicitHeight, font, style.BorderBox, frame.Insets.Vertical) + frame.Insets.Vertical;
            var width = frame.IsAtomic && frame.ShrinkWidth
                ? Math.Min(frame.Box.Width, Limit(content.MaxUsedWidth, style.MinWidth, style.MaxWidth, flow.Width, font, style.BorderBox,
                    frame.Insets.Horizontal) + frame.Insets.Horizontal) : frame.Box.Width;
            SetBounds(content.Plane, frame.Item, new(frame.Box.X, frame.Box.Y, width, outerHeight));
            if (style.Position != HtmlPosition.Static)
                PlacePositioned(content.Position, new(frame.Box.X + frame.Border, frame.Box.Y + frame.Border,
                    Math.Max(0, width - frame.Border * 2), Math.Max(0, outerHeight - frame.Border * 2)));
            var dx = style.Position == HtmlPosition.Relative ? ResolveRelative(style.Left, style.Right, flow.Width, font) : 0;
            var dy = style.Position == HtmlPosition.Relative ? ResolveRelative(style.Top, style.Bottom, flow.ExplicitHeight, font) : 0;
            if (frame.IsAtomic)
            {
                var totalWidth = width + frame.Margin.Horizontal;
                var wrap = CanWrap(frame.Style.WhiteSpace);
                flow.PlacePendingSpace(totalWidth, wrap);
                if (wrap && flow.HasLineContent && flow.Cursor + totalWidth > flow.Width)
                    flow.FlushLine();
                flow.PlaceAtomic(content.Plane, frame.Margin, totalWidth, outerHeight, dx, dy, style.Position == HtmlPosition.Static ? 0 : style.ZIndex ?? 0);
            }
            else
            {
                if (frame.Isolated)
                {
                    content.Plane.X = flow.X + frame.Margin.Left + dx;
                    content.Plane.Y = flow.Y + frame.Margin.Top + dy;
                    flow.Plane.AddNested(content.Plane, style.ZIndex ?? 0);
                }
                flow.Y = (frame.Isolated ? flow.Y + frame.Margin.Top : frame.Box.Y) + outerHeight + frame.Margin.Bottom;
                flow.MaxUsedWidth = Math.Max(flow.MaxUsedWidth, Math.Max(width + frame.Margin.Horizontal,
                    content.MaxUsedWidth + frame.Insets.Horizontal + frame.Margin.Left));
            }
        }

        private static bool IsContainer(HtmlDisplay display) => display is HtmlDisplay.Flex or HtmlDisplay.InlineFlex or HtmlDisplay.Grid or HtmlDisplay.InlineGrid;

        private double? ContentSize(CssLength? specified, double? reference, double font, bool borderBox, double insets)
        {
            if (specified is not { } length || length.Unit == CssUnit.Percent && reference is null) return null;
            return Nonnegative(length.Resolve(reference ?? 0, font, _rootFont) - (borderBox ? insets : 0));
        }

        private double Limit(double size, CssLength? min, CssLength? max, double? reference, double font, bool borderBox, double insets) =>
            Math.Max(ContentSize(min, reference, font, borderBox, insets) ?? 0,
                Math.Min(Nonnegative(size), ContentSize(max, reference, font, borderBox, insets) ?? double.PositiveInfinity));

        private double ResolveRelative(CssLength? start, CssLength? end, double? reference, double font) =>
            ResolvePosition(start, reference, font) ?? -(ResolvePosition(end, reference, font) ?? 0);

        private double? ResolvePosition(CssLength? offset, double? reference, double font) =>
            offset is { } length && (length.Unit != CssUnit.Percent || reference is not null)
                ? length.Resolve(reference ?? 0, font, _rootFont) : null;

        private void QueuePositioned(IElement element, in HtmlStyle style, Flow flow)
        {
            if (_probing) return;
            var target = style.Position == HtmlPosition.Fixed ? _fixed : flow.Position;
            var samePlane = ReferenceEquals(target.Plane, flow.Plane);
            (target.Pending ??= []).Add(new(element, style, samePlane ? flow.X + flow.Cursor : double.NaN,
                samePlane ? flow.Y : double.NaN));
        }

        private void PlacePositioned(PositionContext context, Rect box)
        {
            if (_specialDepth >= 96 || context.Pending is not { } pending) return;
            _specialDepth++;
            try
            {
                // Fixed descendants discovered while laying out a fixed ancestor also belong here.
                for (var i = 0; i < pending.Count; i++)
                {
                    var item = pending[i];
                    var style = item.Style;
                    var font = style.Text.FontSize;
                    var left = ResolvePosition(style.Left, box.Width, font);
                    var right = ResolvePosition(style.Right, box.Width, font);
                    var top = ResolvePosition(style.Top, box.Height, font);
                    var bottom = ResolvePosition(style.Bottom, box.Height, font);
                    var margin = Resolve(style.Margin, box.Width, font);
                    var display = style.Display switch
                    {
                        HtmlDisplay.InlineFlex => HtmlDisplay.Flex, HtmlDisplay.InlineGrid => HtmlDisplay.Grid,
                        HtmlDisplay.Inline or HtmlDisplay.InlineBlock => HtmlDisplay.Block, _ => style.Display
                    };
                    double? width = null;
                    if (style.Width is null)
                        width = left is { } x1 && right is { } x2
                            ? Math.Max(0, box.Width - x1 - x2 - margin.Horizontal)
                            : EstimateWidth(item.Element, style, box.Width);
                    double? height = style.Height is null && top is { } y1 && bottom is { } y2
                        ? Math.Max(0, box.Height - y1 - y2 - margin.Vertical) : null;
                    var parent = new Flow(this, new Plane(), box.X, box.Y, box.Width, box.Height, style, context);
                    var rendered = RenderItem(new(item.Element, null, style with { Display = display }, margin), parent, width, height);
                    rendered.Plane.X = left is { } l ? box.X + l : right is { } r
                        ? box.Right - r - rendered.Width - margin.Horizontal : double.IsNaN(item.StaticX) ? box.X : item.StaticX;
                    rendered.Plane.Y = top is { } t ? box.Y + t : bottom is { } b
                        ? box.Bottom - b - rendered.Height - margin.Vertical : double.IsNaN(item.StaticY) ? box.Y : item.StaticY;
                    context.Plane.AddNested(rendered.Plane, style.ZIndex ?? 0);
                }
            }
            finally { _specialDepth--; }
        }

        private Replacement? GetReplacement(IElement element, in HtmlStyle style)
        {
            if (element.LocalName == "img")
            {
                var w = ImageDimension(element.GetAttribute("width"));
                var h = ImageDimension(element.GetAttribute("height"));
                if (imageSize?.Invoke(element) is { Width: > 0, Height: > 0 } size && double.IsFinite(size.Width) && double.IsFinite(size.Height))
                    return new(new(Nonnegative(size.Width), Nonnegative(size.Height)), w, h, null, false);
                var alt = element.GetAttribute("alt") ?? "";
                var measured = alt.Length > 0 ? MeasureText(alt, style.Text) : default(Size);
                return new(measured, w, h, alt, false);
            }
            var type = element.GetAttribute("type")?.ToLowerInvariant();
            Size? native = element.LocalName switch
            {
                "textarea" => new Size(220, 76), "select" => new Size(150, 28),
                "input" when type is "checkbox" or "radio" => new Size(18, 18),
                "input" when type is not ("hidden" or "button" or "submit" or "reset" or "image" or "file" or "color" or "range") => new Size(180, 28),
                _ => null
            };
            return native is { } control ? new(control, null, null, null, true) : null;
        }

        private static double? ImageDimension(string? value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixels) && pixels >= 0 ? Nonnegative(pixels) : null;

        private sealed class LayoutChild(IElement element, string? text, HtmlStyle style, Edges margin)
        {
            internal readonly IElement Element = element;
            internal readonly string? Text = text;
            internal readonly HtmlStyle Style = style;
            internal Edges Margin = margin;
            internal double Main;
            internal double Weight;
            internal int Row, Column, RowSpan = 1, ColumnSpan = 1;
            internal Rendered? Rendered;
        }

        private sealed class Rendered(Plane plane, Plane boxPlane, double width, double height)
        {
            internal readonly Plane Plane = plane;
            internal double Width = width;
            internal double Height = height;

            internal void StretchHeight(double height)
            {
                if (boxPlane.Items.Count == 0) return;
                var item = boxPlane.Items[0].Item;
                var bounds = item.Bounds;
                if (item.Text is not null) return;
                var control = item.ControlBounds;
                boxPlane.Items[0] = boxPlane.Items[0] with { Item = item with
                {
                    Bounds = new(bounds.X, bounds.Y, bounds.Width, height),
                    ControlBounds = control is { } c ? new(c.X, c.Y, c.Width, Math.Max(0, c.Height + height - Height)) : null
                } };
                Height = height;
            }
        }

        private static bool IsClosedDisclosure(IElement element) =>
            element is IHtmlDetailsElement && !element.HasAttribute("open");

        private List<LayoutChild> Children(IElement element, in HtmlStyle style, Flow content)
        {
            List<LayoutChild> children = [];
            var closed = IsClosedDisclosure(element);
            for (var node = closed ? HtmlPage.FirstSummary(element) : element.FirstChild; node is not null; node = closed ? null : node.NextSibling)
            {
                if (node is IText text && !string.IsNullOrWhiteSpace(text.Data))
                    children.Add(new(element, text.Data, new HtmlStyle(style.Text) { WhiteSpace = style.WhiteSpace, LineHeight = style.LineHeight }, default));
                else if (node is IElement child)
                {
                    var item = styles.Compute(child, style, _rootFont);
                    if (item.Display == HtmlDisplay.None) continue;
                    if (item.Position is HtmlPosition.Absolute or HtmlPosition.Fixed)
                    {
                        QueuePositioned(child, item, content);
                        continue;
                    }
                    item.Display = item.Display switch
                    {
                        HtmlDisplay.InlineFlex => HtmlDisplay.Flex, HtmlDisplay.InlineGrid => HtmlDisplay.Grid,
                        HtmlDisplay.Inline or HtmlDisplay.InlineBlock => HtmlDisplay.Block, _ => item.Display
                    };
                    children.Add(new(child, null, item, Resolve(item.Margin, content.Width, item.Text.FontSize)));
                }
            }
            return children.OrderBy(static child => child.Style.Order).ToList();
        }

        private Rendered RenderItem(LayoutChild child, Flow content, double? width, double? height, double? referenceWidth = null)
        {
            var plane = new Plane();
            var local = new Flow(this, plane, 0, 0, referenceWidth ?? content.Width, content.ExplicitHeight, child.Style, content.Position);
            if (child.Text is { } text)
            {
                local = new Flow(this, plane, 0, 0, width ?? content.Width, null, child.Style, content.Position);
                local.AddText(text, child.Element, child.Style);
                local.FlushLine();
                return new(plane, plane, width ?? local.MaxUsedWidth, height ?? local.Y);
            }
            Walk(child.Element, child.Style, local, rootStyle: child.Style, forceWidth: width, forceHeight: height);
            var boxPlane = plane.Items.Count > 0 && plane.Items[0].Nested is { } nested ? nested : plane;
            var bounds = boxPlane.Items.Count > 0 ? boxPlane.Items[0].Item.Bounds : default;
            return new(plane, boxPlane, bounds.Width, bounds.Height);
        }

        private double Insets(in HtmlStyle style, double reference, bool vertical)
        {
            var font = style.Text.FontSize;
            var padding = Resolve(style.Padding, reference, font);
            return (vertical ? padding.Vertical : padding.Horizontal) + (style.HasBorder ? style.BorderWidth.Resolve(reference, font, _rootFont) * 2 : 0);
        }

        private double EstimateWidth(IElement element, in HtmlStyle style, double reference)
        {
            var padding = Insets(style, reference, false);
            var natural = GetReplacement(element, style);
            var size = natural?.AttributeWidth ?? natural?.Intrinsic?.Width ??
                MeasureText(IsClosedDisclosure(element) ? HtmlPage.FirstSummary(element)?.TextContent ?? "" : element.TextContent, style.Text).Width;
            return padding + Limit(Math.Min(reference, size), style.MinWidth, style.MaxWidth, reference, style.Text.FontSize, style.BorderBox, padding);
        }

        private void LayoutFlex(IElement element, in HtmlStyle style, Flow content)
        {
            var children = Children(element, style, content);
            if (children.Count == 0) return;
            var column = style.FlexColumn;
            var mainSize = column ? content.ExplicitHeight : content.Width;
            var crossSize = column ? content.Width : content.ExplicitHeight;
            var mainGap = (column ? style.RowGap : style.ColumnGap).Resolve(content.Width, style.Text.FontSize, _rootFont);
            var crossGap = (column ? style.ColumnGap : style.RowGap).Resolve(content.Width, style.Text.FontSize, _rootFont);
            var lines = new List<List<LayoutChild>> { new() };
            var lineUsed = 0d;
            foreach (var child in children)
            {
                var item = child.Style;
                var inset = Insets(item, content.Width, column);
                var basis = ContentSize(item.FlexBasis, mainSize, item.Text.FontSize, item.BorderBox, inset) ??
                    ContentSize(column ? item.Height : item.Width, mainSize, item.Text.FontSize, item.BorderBox, inset);
                if (basis is null && column)
                {
                    var width = FlexCrossWidth(child, content, style.AlignItems);
                    _probing = true;
                    try { basis = Math.Max(0, RenderItem(child, content, width, null).Height - inset); }
                    finally { _probing = false; }
                }
                var intrinsic = basis ?? (child.Text is { } text ? MeasureText(text, item.Text).Width
                    : Math.Max(0, EstimateWidth(child.Element, item, content.Width) - inset));
                child.Main = inset + Limit(intrinsic, column ? item.MinHeight : item.MinWidth, column ? item.MaxHeight : item.MaxWidth,
                    mainSize, item.Text.FontSize, item.BorderBox, inset);
                var outer = child.Main + MainMargin(child, column);
                var line = lines[^1];
                if (style.FlexWrap && mainSize is { } available && line.Count > 0 && lineUsed + mainGap + outer > available)
                {
                    lines.Add(line = []);
                    lineUsed = 0;
                }
                lineUsed += outer + (line.Count > 0 ? mainGap : 0);
                line.Add(child);
            }

            var cross = 0d;
            var longest = 0d;
            foreach (var line in lines)
            {
                var totalGap = mainGap * (line.Count - 1);
                var occupied = line.Sum(child => child.Main + MainMargin(child, column)) + totalGap;
                if (mainSize is { } available) FlexSizes(line, available - occupied, column, content);
                occupied = line.Sum(child => child.Main + MainMargin(child, column)) + totalGap;
                var actualMain = mainSize ?? occupied;
                var (lead, extraGap) = Justification(style.JustifyContent, actualMain - occupied, line.Count);
                var lineCross = 0d;
                foreach (var child in line)
                {
                    var desiredWidth = column ? FlexCrossWidth(child, content, style.AlignItems) : child.Main;
                    child.Rendered = RenderItem(child, content, desiredWidth, column ? child.Main : null);
                    var rendered = child.Rendered;
                    lineCross = Math.Max(lineCross, column ? rendered.Width + child.Margin.Horizontal : rendered.Height + child.Margin.Vertical);
                }
                if (lines.Count == 1 && crossSize is { } definite) lineCross = definite;
                var main = lead;
                foreach (var child in line)
                {
                    var rendered = child.Rendered!;
                    var margin = child.Margin;
                    var align = Alignment(child.Style.AlignSelf, style.AlignItems);
                    if (!column && align == HtmlAlign.Stretch && child.Style.Height is null && GetReplacement(child.Element, child.Style) is null)
                    {
                        var padding = Insets(child.Style, content.Width, true);
                        var h = padding + Limit(lineCross - margin.Vertical - padding, child.Style.MinHeight, child.Style.MaxHeight,
                            content.ExplicitHeight, child.Style.Text.FontSize, child.Style.BorderBox, padding);
                        rendered.StretchHeight(h);
                    }
                    var outerMain = child.Main + MainMargin(child, column);
                    var outerCross = column ? rendered.Width + margin.Horizontal : rendered.Height + margin.Vertical;
                    var across = cross + AlignmentOffset(align, lineCross - outerCross);
                    if (style.FlexWrapReverse && crossSize is { } area) across = area - across - outerCross;
                    var along = style.FlexReverse ? actualMain - main - outerMain : main;
                    rendered.Plane.X = content.X + (column ? across : along);
                    rendered.Plane.Y = content.Top + (column ? along : across);
                    main += outerMain + mainGap + extraGap;
                }
                longest = Math.Max(longest, occupied);
                cross += lineCross + crossGap;
            }
            // Preserve source/order-modified order in paint even when geometric order is reversed.
            foreach (var child in children) content.Plane.AddNested(child.Rendered!.Plane);
            content.Y += column ? longest : Math.Max(0, cross - crossGap);
            content.MaxUsedWidth = column ? Math.Max(0, cross - crossGap) : longest;
        }

        private static double MainMargin(LayoutChild item, bool column) => column ? item.Margin.Vertical : item.Margin.Horizontal;

        private double? FlexCrossWidth(LayoutChild child, Flow content, HtmlAlign items)
        {
            var style = child.Style;
            if (style.Width is not null) return null;
            var available = Math.Max(0, content.Width - child.Margin.Horizontal);
            if (child.Text is { } text) return Math.Min(available, MeasureText(text, style.Text).Width);
            var replacement = GetReplacement(child.Element, style);
            if (replacement is not null) return null;
            if (Alignment(style.AlignSelf, items) != HtmlAlign.Stretch) return EstimateWidth(child.Element, style, available);
            var padding = Insets(style, content.Width, false);
            return padding + Limit(available - padding, style.MinWidth, style.MaxWidth, content.Width, style.Text.FontSize, style.BorderBox, padding);
        }

        private void FlexSizes(List<LayoutChild> line, double remaining, bool column, Flow content)
        {
            var grow = remaining > 0;
            foreach (var child in line) child.Weight = grow ? child.Style.FlexGrow : child.Style.FlexShrink * child.Main;
            for (var pass = 0; pass < 16 && Math.Abs(remaining) > 0.001; pass++)
            {
                var total = line.Sum(static child => child.Weight);
                if (total <= 0) break;
                var divisor = grow ? Math.Max(1, total) : total;
                var exceeded = false;
                var consumed = 0d;
                foreach (var child in line)
                {
                    if (child.Weight <= 0) continue;
                    var item = child.Style;
                    var inset = Insets(item, content.Width, column);
                    var delta = remaining * child.Weight / divisor;
                    var proposed = child.Main + delta;
                    var actual = inset + Limit(proposed - inset, column ? item.MinHeight : item.MinWidth,
                        column ? item.MaxHeight : item.MaxWidth, column ? content.ExplicitHeight : content.Width, item.Text.FontSize, item.BorderBox, inset);
                    exceeded |= Math.Abs(actual - proposed) > 0.001;
                    if (Math.Abs(actual - proposed) > 0.001) child.Weight = 0;
                    consumed += actual - child.Main;
                    child.Main = actual;
                }
                if (!exceeded || Math.Abs(consumed) <= 0.001 && line.All(static child => child.Weight == 0)) break;
                remaining -= consumed;
            }
        }

        private static HtmlAlign Alignment(HtmlAlign self, HtmlAlign parent) => self != HtmlAlign.Normal ? self :
            parent != HtmlAlign.Normal ? parent : HtmlAlign.Stretch;

        private static double AlignmentOffset(HtmlAlign align, double space) => align switch
        {
            HtmlAlign.Center => space / 2, HtmlAlign.End => space, _ => 0
        };

        private static (double Lead, double Gap) Justification(HtmlAlign align, double space, int count) => align switch
        {
            HtmlAlign.SpaceBetween when space > 0 && count > 1 => (0, space / (count - 1)),
            HtmlAlign.SpaceAround when space > 0 => (space / (2 * count), space / count),
            HtmlAlign.SpaceEvenly when space > 0 => (space / (count + 1), space / (count + 1)),
            _ => (AlignmentOffset(align, space), 0)
        };

        private void LayoutGrid(IElement element, in HtmlStyle style, Flow content)
        {
            var children = Children(element, style, content);
            var colCount = style.GridColumns?.Length ?? 1;
            foreach (var child in children)
                colCount = Math.Max(colCount, Math.Max(1, child.Style.GridColumn) - 1 + child.Style.GridColumnSpan);
            PlaceGridChildren(children, colCount);
            var rowCount = Math.Max(style.GridRows?.Length ?? 0,
                children.Count == 0 ? 0 : children.Max(child => child.Row + child.RowSpan));
            var colGap = style.ColumnGap.Resolve(content.Width, style.Text.FontSize, _rootFont);
            var rowGap = style.RowGap.Resolve(content.Width, style.Text.FontSize, _rootFont);
            var columns = ResolveTracks(style.GridColumns, colCount, content.Width, colGap, style.Text.FontSize, stretchAuto: true);
            var rows = ResolveTracks(style.GridRows, rowCount, content.ExplicitHeight, rowGap, style.Text.FontSize, stretchAuto: false);
            var columnStarts = TrackStarts(columns, colGap);
            foreach (var child in children)
            {
                var item = child.Style;
                var available = SpanSize(columnStarts, child.Column, child.ColumnSpan, colGap);
                child.Margin = Resolve(item.Margin, available, item.Text.FontSize);
                var inset = Insets(item, available, false);
                var width = item.Width is null && GetReplacement(child.Element, item) is null
                    ? Limit(available - child.Margin.Horizontal - inset, item.MinWidth, item.MaxWidth, available, item.Text.FontSize, item.BorderBox, inset) + inset
                    : (double?)null;
                child.Rendered = RenderItem(child, content, width, null, available);
                var height = child.Rendered.Height + child.Margin.Vertical;
                var occupied = rowGap * (child.RowSpan - 1);
                var autoRows = 0;
                for (var r = child.Row; r < child.Row + child.RowSpan; r++)
                {
                    occupied += rows[r];
                    if (IsAutoRow(r, style.GridRows, content.ExplicitHeight)) autoRows++;
                }
                if (height > occupied && autoRows != 0)
                    for (var r = child.Row; r < child.Row + child.RowSpan; r++)
                        if (IsAutoRow(r, style.GridRows, content.ExplicitHeight)) rows[r] += (height - occupied) / autoRows;
            }
            var rowStarts = TrackStarts(rows, rowGap);
            foreach (var child in children)
            {
                var item = child.Style;
                var rendered = child.Rendered!;
                var availableHeight = SpanSize(rowStarts, child.Row, child.RowSpan, rowGap);
                var align = Alignment(item.AlignSelf, style.AlignItems);
                if (align == HtmlAlign.Stretch && item.Height is null && GetReplacement(child.Element, item) is null && child.Text is null)
                {
                    var inset = Insets(item, SpanSize(columnStarts, child.Column, child.ColumnSpan, colGap), true);
                    var target = inset + Limit(availableHeight - child.Margin.Vertical - inset, item.MinHeight, item.MaxHeight,
                        content.ExplicitHeight, item.Text.FontSize, item.BorderBox, inset);
                    rendered.StretchHeight(target);
                }
                rendered.Plane.X = content.X + columnStarts[child.Column];
                rendered.Plane.Y = content.Top + rowStarts[child.Row] + AlignmentOffset(align, availableHeight - rendered.Height - child.Margin.Vertical);
                content.Plane.AddNested(rendered.Plane);
            }
            content.Y += rows.Length == 0 ? 0 : rowStarts[^1] - rowGap;
            content.MaxUsedWidth = columnStarts[^1] - colGap;
        }

        private static void PlaceGridChildren(List<LayoutChild> children, int columns)
        {
            var occupied = new HashSet<(int Row, int Column)>();
            foreach (var child in children)
            {
                child.Row = child.Style.GridRow - 1;
                child.Column = child.Style.GridColumn - 1;
                child.RowSpan = child.Style.GridRowSpan;
                child.ColumnSpan = child.Style.GridColumnSpan;
                if (child.Row >= 0 && child.Column >= 0) Reserve(child);
            }
            var autoRow = 0;
            var autoColumn = 0;
            foreach (var child in children)
            {
                if (child.Row >= 0 && child.Column >= 0) continue;
                var row = child.Row >= 0 ? child.Row : child.Column >= 0 ? 0 : autoRow;
                var column = child.Column >= 0 ? child.Column : child.Row >= 0 ? 0 : autoColumn;
                while (column + child.ColumnSpan > columns || Intersects(child, row, column))
                {
                    if (child.Column >= 0) row++;
                    else if (column + child.ColumnSpan >= columns)
                    {
                        row++;
                        column = 0;
                    }
                    else column++;
                }
                if (child.Row < 0 && child.Column < 0)
                {
                    autoRow = row;
                    autoColumn = column + child.ColumnSpan;
                }
                child.Row = row;
                child.Column = column;
                Reserve(child);
            }

            bool Intersects(LayoutChild child, int row, int col)
            {
                for (var r = row; r < row + child.RowSpan; r++)
                    for (var c = col; c < col + child.ColumnSpan; c++)
                        if (occupied.Contains((r, c))) return true;
                return false;
            }

            void Reserve(LayoutChild child)
            {
                for (var r = child.Row; r < child.Row + child.RowSpan; r++)
                    for (var c = child.Column; c < child.Column + child.ColumnSpan; c++)
                        occupied.Add((r, c));
            }
        }

        private double[] ResolveTracks(CssTrack[]? configured, int count, double? available, double gap, double font, bool stretchAuto)
        {
            var sizes = new double[count];
            var remaining = (available ?? 0) - gap * Math.Max(0, count - 1);
            var weight = 0d;
            var auto = 0;
            for (var i = 0; i < count; i++)
            {
                var track = configured is not null && i < configured.Length ? configured[i] : default;
                sizes[i] = Math.Max(ContentSize(track.Length, available, font, false, 0) ?? 0,
                    ContentSize(track.Minimum, available, font, false, 0) ?? 0);
                remaining -= sizes[i];
                weight += track.Fraction;
                if (track.Length is null && track.Fraction == 0) auto++;
            }
            if (remaining > 0 && (weight > 0 || stretchAuto && auto > 0))
            {
                for (var i = 0; i < count; i++)
                {
                    var track = configured is not null && i < configured.Length ? configured[i] : default;
                    if (weight > 0) sizes[i] += remaining * track.Fraction / Math.Max(1, weight);
                    else if (track.Length is null) sizes[i] += remaining / auto;
                }
            }
            return sizes;
        }

        private static bool IsAutoRow(int row, CssTrack[]? tracks, double? height) => tracks is null || row >= tracks.Length ||
            (height is null && (tracks[row].Length is null || tracks[row].Length?.Unit == CssUnit.Percent)) ||
            (tracks[row].Length is null && tracks[row].Fraction == 0);

        private static double[] TrackStarts(double[] sizes, double gap)
        {
            var starts = new double[sizes.Length + 1];
            for (var i = 0; i < sizes.Length; i++) starts[i + 1] = starts[i] + sizes[i] + gap;
            return starts;
        }

        private static double SpanSize(double[] starts, int start, int count, double gap) => starts[start + count] - starts[start] - gap;

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

        private readonly record struct PaintEntry(HtmlDrawItem Item, Plane? Nested = null, int ZIndex = 0);
        private readonly record struct PaintFrame(IReadOnlyList<PaintEntry> Items, int Next, double X, double Y);
        private readonly record struct Edges(double Top, double Right, double Bottom, double Left)
        {
            internal double Horizontal => Left + Right;
            internal double Vertical => Top + Bottom;
        }

        private readonly record struct Frame(IElement Element, INode? Next, HtmlStyle Style, Flow Flow, Flow Content,
            int Item, Rect Box, Edges Margin, Edges Insets, SpanBox? Span, bool Isolated = false, bool IsAtomic = false,
            double Border = 0, bool ShrinkWidth = false, bool OnlyChild = false);

        private sealed class Plane
        {
            internal readonly List<PaintEntry> Items = [];
            internal double X;
            internal double Y;
            private bool _sort;
            internal IReadOnlyList<PaintEntry> PaintItems => !_sort ? Items : Items[0].Nested is null
                ? Items.Take(1).Concat(Items.Skip(1).OrderBy(entry => entry.ZIndex)).ToArray()
                : Items.OrderBy(entry => entry.ZIndex).ToArray();

            internal void AddNested(Plane nested, int zIndex = 0)
            {
                Items.Add(new(default, nested, zIndex));
                _sort |= zIndex != 0;
            }
        }

        private sealed class PositionContext(Plane plane)
        {
            internal readonly Plane Plane = plane;
            internal List<Positioned>? Pending;
        }

        private readonly record struct Positioned(IElement Element, HtmlStyle Style, double StaticX, double StaticY);
        private readonly record struct Replacement(Size? Intrinsic, double? AttributeWidth, double? AttributeHeight, string? Alt, bool Control);

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

        private sealed class Flow(Builder owner, Plane plane, double x, double y, double width, double? explicitHeight, HtmlStyle style,
            PositionContext position)
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
            internal PositionContext Position { get; } = position;
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

            internal void PlaceAtomic(Plane nested, Edges margin, double width, double height, double dx, double dy, int zIndex)
            {
                BeginLine();
                FlushText();
                nested.X = X + Cursor + margin.Left + dx;
                nested.Y = Y + margin.Top + SpanTop + dy;
                Plane.AddNested(nested, zIndex);
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
