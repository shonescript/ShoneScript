using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;
using IElement = AngleSharp.Dom.IElement;

namespace ShoneHtml;

/// <summary>Displays an HTML page inside a MewUI window. The page can be shared by multiple views.</summary>
public sealed class HtmlView : FrameworkElement
{
    private const double WheelStep = 48;
    private const int MaxCachedTextLayouts = 2048;
    private const int MaxCachedTextLength = 256;

    private readonly HtmlLayout _layout = new();
    private readonly Dictionary<TextKey, ITextLayout> _textLayouts = new();
    private Window? _window;
    private bool _observingPage;
    private IElement? _pressedElement;
    private double _scrollOffset;

    public HtmlView(HtmlPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Page = page;
    }

    public HtmlPage Page { get; }

    internal Action<Window>? SynchronizeTitle { get; init; }

    // The view is a viewport. Its desired size is independent of document height.
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : 480,
        double.IsFinite(availableSize.Height) ? availableSize.Height : 320);

    protected override void OnVisualRootChanged(Element? oldRoot, Element? newRoot)
    {
        base.OnVisualRootChanged(oldRoot, newRoot);
        StopObservingWindow();
        if (newRoot is not Window window)
            return;

        _window = window;
        window.Loaded += ObservePage;
        window.Closed += StopObservingWindow;
        if (window.Handle != IntPtr.Zero)
            ObservePage();
    }

    protected override void OnDispose()
    {
        StopObservingWindow();
        base.OnDispose();
    }

    protected override void OnDpiChanged(uint oldDpi, uint newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearLayout();
        InvalidateVisual();
    }

    protected override void OnThemeChanged(Theme oldTheme, Theme newTheme)
    {
        base.OnThemeChanged(oldTheme, newTheme);
        if (oldTheme?.Metrics.FontFamily != newTheme.Metrics.FontFamily)
        {
            ClearLayout();
            InvalidateVisual();
        }
    }

    protected override void OnRender(IGraphicsContext context)
    {
        base.OnRender(context);
        if (Bounds.IsEmpty)
            return;

        var layout = GetLayout();
        context.Save();
        try
        {
            context.IntersectClip(Bounds);
            foreach (var item in layout.GetVisibleItems(_scrollOffset, _scrollOffset + Bounds.Height))
            {
                var rect = item.Bounds.Offset(Bounds.X, Bounds.Y - _scrollOffset);
                if (!rect.IntersectsWith(Bounds))
                    continue;

                if (item.Background is { A: > 0 } background)
                    context.FillRectangle(rect, background);
                if (item.BorderWidth > 0 && item.BorderColor.A > 0)
                    context.DrawRectangle(rect, item.BorderColor, item.BorderWidth, strokeInset: true);
                if (item.Text is { Length: > 0 } text)
                {
                    context.Text.Draw(GetTextLayout(text, item.TextStyle), rect.TopLeft,
                        new TextDrawOptions(item.TextStyle.Color));
                }
            }
        }
        finally
        {
            context.Restore();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        RecordPress(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        RecordPress(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButton.Left)
            return;

        var pressedElement = _pressedElement;
        _pressedElement = null;
        if (!e.Handled && pressedElement is not null && ReferenceEquals(pressedElement, HitTestPage(e)))
        {
            e.Handled = true;
            Page.Click(pressedElement);
        }
    }

    protected override void OnMouseLeave()
    {
        base.OnMouseLeave();
        _pressedElement = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || e.Delta.Y == 0 || Bounds.IsEmpty)
            return;

        var maxOffset = Math.Max(0, GetLayout().Height - Bounds.Height);
        var offset = Math.Clamp(_scrollOffset - e.Delta.Y * WheelStep, 0, maxOffset);
        if (offset == _scrollOffset)
            return;

        _scrollOffset = offset;
        _pressedElement = null;
        e.Handled = true;
        InvalidateVisual();
    }

    private void RecordPress(MouseEventArgs e)
    {
        if (e.Handled || e.Button != MouseButton.Left)
            return;

        _pressedElement = HitTestPage(e);
        e.Handled = _pressedElement is not null;
    }

    private IElement? HitTestPage(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return !Bounds.IsEmpty && new Rect(Bounds.Size).Contains(point)
            ? GetLayout().HitTest(point.X, point.Y + _scrollOffset)
            : null;
    }

    private HtmlLayoutResult GetLayout()
    {
        var layout = _layout.Get(Page.Document, Page.Version, Bounds.Width, MeasureText);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, layout.Height - Bounds.Height));
        return layout;
    }

    private Size MeasureText(string text, HtmlTextStyle style) => GetTextLayout(text, style).MeasuredSize;

    private ITextLayout GetTextLayout(string text, HtmlTextStyle style)
    {
        var fontFamily = style.FontFamily ?? Theme.Metrics.FontFamily;
        var runStyle = new TextRunStyle(fontFamily, style.FontSize,
            style.Bold ? FontWeight.Bold : FontWeight.Normal, style.Italic);
        var dpi = GetDpi();
        var key = new TextKey(text, runStyle, dpi);
        if (_textLayouts.TryGetValue(key, out var layout))
            return layout;

        layout = GetGraphicsFactory().TextEngine.CreateLayout(new TextLayoutRequest
        {
            Text = text.AsMemory(),
            DefaultStyle = runStyle,
            Dpi = dpi
        });

        if (text.Length <= MaxCachedTextLength)
        {
            if (_textLayouts.Count == MaxCachedTextLayouts)
                _textLayouts.Clear();
            _textLayouts.Add(key, layout);
        }
        return layout;
    }

    private void ObservePage()
    {
        if (_observingPage)
            return;

        _observingPage = true;
        Page.Changed += OnPageChanged;
        OnPageChanged();
    }

    private void OnPageChanged()
    {
        if (_window is { } window)
            SynchronizeTitle?.Invoke(window);
        InvalidateVisual();
    }

    private void StopObservingWindow()
    {
        if (_window is { } window)
        {
            window.Loaded -= ObservePage;
            window.Closed -= StopObservingWindow;
            _window = null;
        }
        if (_observingPage)
        {
            Page.Changed -= OnPageChanged;
            _observingPage = false;
        }
        _pressedElement = null;
        ClearLayout();
    }

    private void ClearLayout()
    {
        _layout.Clear();
        _textLayouts.Clear();
    }

    private readonly record struct TextKey(string Text, TextRunStyle Style, uint Dpi);
}
