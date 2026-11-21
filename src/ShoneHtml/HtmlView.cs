using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Element = Aprillz.MewUI.Controls.Element;
using IElement = AngleSharp.Dom.IElement;

namespace ShoneHtml;

/// <summary>Displays an HTML page inside a MewUI window.</summary>
public sealed class HtmlView : Panel
{
    private const double WheelStep = 48;
    private const double ScrollBarWidth = 14;
    // Scrollbars are overlay controls: keep the track transparent and draw only the thumb.
    private static readonly Color ScrollBarTrack = Color.FromArgb(0, 0, 0, 0);
    private const int MaxCachedTextLayouts = 2048;
    private const int MaxCachedTextLength = 256;
    private const int MaxDecodedImagePixels = 16_000_000;
    private readonly HtmlLayout _layout = new();
    private readonly Dictionary<TextKey, ITextLayout> _textLayouts = new();
    private readonly Dictionary<byte[], PageImage> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IElement, Field> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly ScrollBar _verticalBar = NewBar(Orientation.Vertical);
    private readonly ScrollBar _horizontalBar = NewBar(Orientation.Horizontal);
    private readonly Dictionary<object, Point> _elementOffsets = new();
    private readonly Dictionary<object, ElementBars> _elementBars = new();
    private readonly Dictionary<IElement, byte[]> _canvasSources = new(ReferenceEqualityComparer.Instance);
    private ViewportLayoutKey _viewportKey;
    private HtmlLayoutResult? _viewportLayout;
    private Rect _viewport;
    private bool _verticalGutter, _horizontalGutter;
    private readonly HtmlBrowser? _browser;
    private readonly HtmlPage? _startPage;
    private Window? _window;
    private bool _observingPage;
    private bool _syncingScrollBar;
    private bool _movingFocus;
    private bool? _tabEntryReverse;
    private IElement? _pressedElement;
    private IElement? _hoveredElement;
    private IElement? _focusedElement;
    private Point _scrollOffset;
    private IElement? _keyboardScrollTarget;
    private long _navigationVersion = -1;
    private bool _restoreScroll;
    private string? _fragment;
    private double _pendingWheelX, _pendingWheelY;
    private Point _pendingWheelPoint;
    private bool _wheelDispatchPending;

    public HtmlView(HtmlPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Page = page;
        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;
        _verticalBar.ValueChanged += value =>
        {
            if (!_syncingScrollBar) ScrollTo(new(_scrollOffset.X, value));
        };
        _horizontalBar.ValueChanged += value =>
        {
            if (!_syncingScrollBar) ScrollTo(new(value, _scrollOffset.Y));
        };
        Add(_horizontalBar);
        Add(_verticalBar);
    }

    /// <summary>Displays the live page of a browser whose context is bound to the UI thread.</summary>
    public HtmlView(HtmlBrowser browser, HtmlPage? startPage = null) : this(startPage ?? HtmlPage.Parse(""))
    {
        ArgumentNullException.ThrowIfNull(browser);
        _browser = browser;
        _startPage = Page;
        Page = browser.Page ?? Page;
    }

    public HtmlPage Page { get; private set; }
    /// <summary>Occurs when the displayed page changes or publishes document changes.</summary>
    public event Action? PageChanged;
    internal Action<Window>? SynchronizeTitle { get; init; }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? availableSize.Width : 480,
        double.IsFinite(availableSize.Height) ? availableSize.Height : 320);

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_window is not null && finalSize.Width > 0 && finalSize.Height > 0)
            ArrangeViewport(finalSize);
        return finalSize;
    }

    internal void ArrangeViewport(Size finalSize)
    {
        var layout = GetLayout(finalSize);
        var extent = ViewportExtent(layout);
        _scrollOffset = Clamp(_scrollOffset, extent);
        ReconcileElementOffsets(layout);
        if (_restoreScroll)
        {
            _restoreScroll = false;
            if (_fragment is { Length: > 0 } fragment &&
                (Page.Document.GetElementById(fragment) ?? Page.Document.QuerySelectorAll("a[name]")
                    .FirstOrDefault(element => element.GetAttribute("name") == fragment)) is { } target &&
                FindBounds(layout, target) is { } item)
                Reveal(layout, item, alignTop: true);
            _fragment = null;
        }

        var frame = CreateFrame(layout);
        ArrangeFields(frame);
        _syncingScrollBar = true;
        try
        {
            ArrangeBar(_horizontalBar, _horizontalGutter, new(0, _viewport.Height, _viewport.Width, finalSize.Height - _viewport.Height),
                extent.X, _viewport.Width, _scrollOffset.X);
            ArrangeBar(_verticalBar, _verticalGutter, new(_viewport.Width, 0, finalSize.Width - _viewport.Width, _viewport.Height),
                extent.Y, _viewport.Height, _scrollOffset.Y);
            ArrangeElementBars(layout, frame);
        }
        finally
        {
            _syncingScrollBar = false;
        }

        _browser?.UpdateScroll(_scrollOffset.X, _scrollOffset.Y);
    }

    protected override void OnVisualRootChanged(Element? oldRoot, Element? newRoot)
    {
        base.OnVisualRootChanged(oldRoot, newRoot);
        StopObservingWindow();
        if (newRoot is not Window window)
            return;
        _window = window;
        window.Loaded += ObservePage;
        window.Closed += StopObservingWindow;
        window.PreviewKeyDown += PreviewKeyDown;
        if (window.Handle != IntPtr.Zero)
            ObservePage();
    }

    protected override void OnDispose()
    {
        StopObservingWindow();
        _verticalBar.Dispose();
        _horizontalBar.Dispose();
        base.OnDispose();
    }

    protected override void OnDpiChanged(uint oldDpi, uint newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ClearLayout();
        RefreshLayout();
    }

    protected override void OnThemeChanged(Theme oldTheme, Theme newTheme)
    {
        base.OnThemeChanged(oldTheme, newTheme);
        if (oldTheme?.Metrics.FontFamily != newTheme.Metrics.FontFamily || oldTheme?.IsDark != newTheme.IsDark)
        {
            ClearLayout();
            RefreshLayout();
        }
    }

    protected override void OnRender(IGraphicsContext context)
    {
        base.OnRender(context);
        if (_window is not null && !Bounds.IsEmpty)
            // A page without an explicit CSS background uses the dark browser canvas.
            context.FillRectangle(Bounds, Theme.IsDark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(255, 255, 255));
    }

    protected override void RenderSubtree(IGraphicsContext context)
    {
        if (_window is null || Bounds.IsEmpty) return;
        var frame = CreateFrame(GetLayout(Bounds.Size));
        var paintedFlowBars = false;
        foreach (var item in frame.VisibleItems())
        {
            if (item.Fixed && !paintedFlowBars)
            {
                PaintElementBars(fixedPosition: false);
                paintedFlowBars = true;
            }
            var (bounds, clip) = frame.Place(item);
            var rect = ToWindow(bounds);
            context.Save();
            try
            {
                context.IntersectClip(ToWindow(clip));
                if (item.Background is { A: > 0 } background)
                    context.FillRectangle(rect, background);
                if (item.BorderWidth > 0 && item.BorderColor.A > 0)
                    context.DrawRectangle(rect, item.BorderColor, item.BorderWidth, strokeInset: true);
                if (item.ImageBounds is { } imageBounds && GetImage(item.Element) is { } image)
                    context.DrawImage(image, ToWindow(frame.Place(item, imageBounds).Bounds));
                if (item.Text is { Length: > 0 } text)
                    context.Text.Draw(GetTextLayout(text, item.TextStyle), rect.TopLeft,
                        new TextDrawOptions(item.TextStyle.Color));
                if (item.ControlBounds is { } && _fields.TryGetValue(item.Element, out var field))
                    field.Host.Render(context);
                else if (ReferenceEquals(_focusedElement, item.Element) && IsFocused)
                    context.DrawRectangle(rect, Theme.Palette.Accent, 1, strokeInset: true);
            }
            finally { context.Restore(); }
        }
        if (!paintedFlowBars) PaintElementBars(fixedPosition: false);
        PaintElementBars(fixedPosition: true);
        foreach (var child in Children)
        {
            if (child is ClipHost) continue;
            if (child is ScrollBar bar && bar.IsVisible && ScrollBarTrack.A > 0)
                context.FillRectangle(bar.Bounds, ScrollBarTrack);
            child.Render(context);
        }

        void PaintElementBars(bool fixedPosition)
        {
            foreach (var bars in _elementBars.Values)
            {
                if (bars.Region.Fixed != fixedPosition) continue;
                PaintBar(bars.Horizontal, bars.HorizontalHost);
                PaintBar(bars.Vertical, bars.VerticalHost);
            }
        }

        void PaintBar(ScrollBar bar, ClipHost host)
        {
            if (bar.IsVisible)
            {
                if (ScrollBarTrack.A > 0)
                    context.FillRectangle(host.Bounds, ScrollBarTrack);
                host.Render(context);
            }
        }
    }

    protected override UIElement? OnHitTest(Point point)
    {
        if (_window is null || Bounds.IsEmpty) return base.OnHitTest(point);
        if (!Bounds.Contains(point)) return null;
        for (var i = Count - 1; i >= 0; i--)
            if (this[i] is UIElement child and not ClipHost && child.HitTest(point) is { } chrome)
                return chrome;

        var frame = CreateFrame(GetLayout(Bounds.Size));
        var item = frame.HitTest(new(point.X - Bounds.X, point.Y - Bounds.Y));
        var fixedBar = HitElementBars(fixedPosition: true);
        if (fixedBar is not null) return fixedBar;
        if (item is not { Fixed: true } && HitElementBars(fixedPosition: false) is { } flowBar) return flowBar;
        if (item is { ControlBounds: not null } box && _fields.TryGetValue(box.Element, out var field))
            return field.Host.HitTest(point) ?? this;
        return this;

        UIElement? HitElementBars(bool fixedPosition)
        {
            for (var i = Count - 1; i >= 0; i--)
                if (this[i] is ClipHost { BarRegion: { } region } host && region.Fixed == fixedPosition &&
                    host.HitTest(point) is { } hit) return hit;
            return null;
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
        if (e.Button != MouseButton.Left || _pressedElement is not { } pressed)
            return;
        _pressedElement = null;
        RefreshLayout();
        if (!e.Handled && ReferenceEquals(pressed, HitTestPage(e)))
        {
            e.Handled = true;
            Activate(pressed);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hovered = HitTestPage(e);
        if (ReferenceEquals(hovered, _hoveredElement))
            return;
        _hoveredElement = hovered;
        Cursor = Ancestor(hovered, element => element.LocalName == "a" && element.HasAttribute("href")) is null
            ? null : CursorType.Hand;
        RefreshLayout();
    }

    protected override void OnMouseLeave()
    {
        base.OnMouseLeave();
        _pressedElement = _hoveredElement = null;
        Cursor = null;
        RefreshLayout();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Handled || _window is null || Bounds.IsEmpty)
            return;

        // Wheel messages often arrive in a burst. Coalesce them into one layout/paint pass;
        // this keeps wheel scrolling close to the smoothness of thumb dragging on long pages.
        var dx = -e.Delta.X * WheelStep;
        var dy = -e.Delta.Y * WheelStep;
        if (e.ShiftKey && dx == 0) (dx, dy) = (dy, 0);
        _pendingWheelX += dx;
        _pendingWheelY += dy;
        _pendingWheelPoint = e.GetPosition(this);
        if (!_wheelDispatchPending)
        {
            _wheelDispatchPending = true;
            (Application.Current.Dispatcher ?? throw new InvalidOperationException("No UI dispatcher is available.")).BeginInvoke(ApplyPendingWheel);
        }
        e.Handled = dx != 0 || dy != 0;
    }

    private void ApplyPendingWheel()
    {
        _wheelDispatchPending = false;
        var dx = _pendingWheelX;
        var dy = _pendingWheelY;
        _pendingWheelX = _pendingWheelY = 0;
        if (_window is null || Bounds.IsEmpty || dx == 0 && dy == 0)
            return;
        var layout = GetLayout(Bounds.Size);
        var hit = CreateFrame(layout).HitTest(_pendingWheelPoint);
        var start = hit is { } item ? ScrollTarget(layout, item) : null;
        var handledX = dx != 0 && ScrollChain(layout, start, horizontal: true, _ => dx);
        var handledY = dy != 0 && ScrollChain(layout, start, horizontal: false, _ => dy);
        if (!handledX && !handledY)
            RefreshLayout();
    }

    protected override void OnGotFocus()
    {
        base.OnGotFocus();
        if (_movingFocus || _tabEntryReverse is not { } reverse)
            return;
        _tabEntryReverse = null;
        var page = Page;
        Application.Current.Dispatcher?.BeginInvoke(() =>
        {
            if (ReferenceEquals(Page, page) && IsFocused)
                MovePageFocus(reverse);
        });
    }

    protected override void OnLostFocus()
    {
        base.OnLostFocus();
        if (!_movingFocus)
            SetPageFocus(null);
    }

    private void PreviewKeyDown(KeyEventArgs e)
    {
        if (e.Handled)
            return;
        if (e.Key == Key.Tab && !e.ControlKey && !e.AltKey && !e.MetaKey)
        {
            if (!IsFocusWithin && !IsFocused)
            {
                _tabEntryReverse = e.ShiftKey;
                return;
            }
            if (MovePageFocus(e.ShiftKey))
            {
                e.Handled = true;
                return;
            }
            // At the boundary, let MewUI traverse to the browser chrome.
            if (_window?.Content == this)
            {
                SetPageFocus(null);
                e.Handled = MovePageFocus(e.ShiftKey);
            }
            else
                FocusView(null);
            return;
        }
        _tabEntryReverse = null;
        if (!IsFocused)
            return;
        if (_focusedElement is { } focused && (e.Key == Key.Enter || e.Key == Key.Space && focused.LocalName != "a"))
        {
            e.Handled = true;
            Activate(focused);
            return;
        }
        if (e.ControlKey || e.AltKey || e.MetaKey || Bounds.IsEmpty || _window is null ||
            e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Space or Key.Home or Key.End))
            return;
        var layout = GetLayout(Bounds.Size);
        var target = _focusedElement ?? _keyboardScrollTarget;
        var region = target is not null && FindBounds(layout, target) is { } item ? ScrollTarget(layout, item) : null;
        e.Handled = ScrollChain(layout, region, e.Key is Key.Left or Key.Right, pageSize => e.Key switch
        {
            Key.Left or Key.Up => -WheelStep,
            Key.Right or Key.Down => WheelStep,
            Key.PageUp => -pageSize,
            Key.PageDown or Key.Space => e.ShiftKey ? -pageSize : pageSize,
            Key.Home => double.NegativeInfinity,
            Key.End => double.PositiveInfinity,
            _ => 0
        });
    }

    private bool MovePageFocus(bool reverse)
    {
        if (_window is null || Bounds.IsEmpty)
            return false;
        var layout = GetLayout(Bounds.Size);
        var placed = new Dictionary<IElement, HtmlDrawItem>(ReferenceEqualityComparer.Instance);
        foreach (var box in layout.Items)
        {
            for (var current = box.Element; current is not null; current = current.ParentElement)
                if (!placed.TryAdd(current, box))
                    break;
            if (box.ControlBounds is not null)
                placed[box.Element] = box;
        }
        var sequence = Page.Document.QuerySelectorAll("a[href],button,input,select,textarea,summary,[tabindex]")
            .Where(element => IsFocusable(element) && TabIndexOf(element) >= 0 && placed.ContainsKey(element))
            .OrderBy(element => TabIndexOf(element) is > 0 and var index ? index : int.MaxValue)
            .ToList();
        var currentIndex = _focusedElement is null ? -1 : sequence.IndexOf(_focusedElement);
        var next = reverse ? (currentIndex < 0 ? sequence.Count - 1 : currentIndex - 1) : currentIndex + 1;
        if (next < 0 || next >= sequence.Count)
            return false;
        var target = sequence[next];
        FocusPageItem(target, placed[target]);
        return true;
    }

    private void FocusPageItem(IElement target, HtmlDrawItem item)
    {
        var layout = GetLayout(Bounds.Size);
        Reveal(layout, item);
        if (item.ControlBounds is { } controlBounds)
        {
            // An offscreen input can enter the tab sequence before the next frame arranges it.
            var field = GetField(target);
            var (rect, clip) = CreateFrame(layout).Place(item, controlBounds);
            ArrangeField(field, rect, clip);
            field.Control.Focus();
        }
        else
            FocusView(target);
    }

    private void FocusView(IElement? element)
    {
        _movingFocus = true;
        try { Focus(); }
        finally { _movingFocus = false; }
        SetPageFocus(element);
    }

    private void SetPageFocus(IElement? element)
    {
        if (ReferenceEquals(element, _focusedElement))
            return;
        _focusedElement = element;
        Page.SetFocusedElement(element);
        RefreshLayout();
    }

    private void RecordPress(MouseEventArgs e)
    {
        if (e.Handled || e.Button != MouseButton.Left)
            return;
        _tabEntryReverse = null;
        _pressedElement = HitTestPage(e);
        _keyboardScrollTarget = _pressedElement;
        FocusView(Ancestor(_pressedElement, IsFocusable));
        RefreshLayout();
        e.Handled = _pressedElement is not null;
    }

    private IElement? HitTestPage(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return _window is not null && !Bounds.IsEmpty
            ? CreateFrame(GetLayout(Bounds.Size)).HitTest(point)?.Element : null;
    }

    private bool ScrollTo(Point offset)
    {
        if (_window is null || Bounds.IsEmpty)
            return false;
        offset = Clamp(offset, ViewportExtent(GetLayout(Bounds.Size)));
        if (offset == _scrollOffset)
            return false;
        _scrollOffset = offset;
        _browser?.UpdateScroll(offset.X, offset.Y);
        OnScrolled();
        return true;
    }

    private void OnScrolled()
    {
        _pressedElement = null;
        RefreshLayout();
    }

    private object ScrollKey(IElement element) => Page.IsRendererMirror && Page.GetRendererId(element) is > 0 and var id ? id : element;
    internal Point GetElementOffset(IElement element) => _elementOffsets.GetValueOrDefault(ScrollKey(element));

    internal bool ScrollElement(HtmlScrollRegion region, Point offset)
    {
        var key = ScrollKey(region.Element);
        offset = region.Clamp(offset);
        if (offset == _elementOffsets.GetValueOrDefault(key))
            return false;
        if (offset == default) _elementOffsets.Remove(key);
        else _elementOffsets[key] = offset;
        OnScrolled();
        return true;
    }

    internal void ReconcileElementOffsets(HtmlLayoutResult layout)
    {
        if (_elementOffsets.Count == 0) return;
        var keys = new HashSet<object>();
        foreach (var region in layout.ScrollRegions)
        {
            var key = ScrollKey(region.Element);
            if (!_elementOffsets.TryGetValue(key, out var previous)) continue;
            keys.Add(key);
            _elementOffsets[key] = region.Clamp(previous);
        }
        foreach (var key in _elementOffsets.Keys.ToArray())
            if (!keys.Contains(key) || _elementOffsets[key] == default) _elementOffsets.Remove(key);
    }

    private static HtmlScrollRegion? ScrollTarget(HtmlLayoutResult layout, HtmlDrawItem item) =>
        layout.RegionsByElement.TryGetValue(item.Element, out var own) && ReferenceEquals(own.Parent, item.ScrollParent)
            ? own : item.ScrollParent;

    private bool ScrollChain(HtmlLayoutResult layout, HtmlScrollRegion? start, bool horizontal, Func<double, double> delta)
    {
        for (var region = start; region is not null; region = region.Parent)
        {
            var offset = GetElementOffset(region.Element);
            if (horizontal ? region.UserX : region.UserY)
                if (ScrollElement(region, horizontal ? new(offset.X + delta(region.Port.Width), offset.Y)
                    : new(offset.X, offset.Y + delta(region.Port.Height))))
                    return true;
        }
        if ((horizontal ? layout.ViewportX : layout.ViewportY) == HtmlOverflow.Hidden) return false;
        return ScrollTo(horizontal ? new(_scrollOffset.X + delta(_viewport.Width), _scrollOffset.Y)
            : new(_scrollOffset.X, _scrollOffset.Y + delta(_viewport.Height)));
    }

    private void Reveal(HtmlLayoutResult layout, HtmlDrawItem item, bool alignTop = false)
    {
        var target = item.ControlBounds ?? item.Bounds;
        for (var region = item.ScrollParent; region is not null; region = region.Parent)
        {
            var port = region.Port;
            var offset = GetElementOffset(region.Element);
            var desired = region.Clamp(new(
                RevealAxis(target.Left - port.Left, target.Right - port.Left, port.Width, offset.X),
                RevealAxis(target.Top - port.Top, target.Bottom - port.Top, port.Height, offset.Y, alignTop)));
            ScrollElement(region, desired);
            target = target.Offset(-desired.X, -desired.Y);
            // A clipped descendant represents only the portion inside this scrollport to outer ancestors.
            var left = region.X == HtmlOverflow.Visible ? target.Left : Math.Clamp(target.Left, port.Left, port.Right);
            var top = region.Y == HtmlOverflow.Visible ? target.Top : Math.Clamp(target.Top, port.Top, port.Bottom);
            var right = region.X == HtmlOverflow.Visible ? target.Right : Math.Clamp(target.Right, port.Left, port.Right);
            var bottom = region.Y == HtmlOverflow.Visible ? target.Bottom : Math.Clamp(target.Bottom, port.Top, port.Bottom);
            target = new(left, top, right - left, bottom - top);
        }
        if (!item.Fixed)
        {
            var offset = Clamp(new(RevealAxis(target.Left, target.Right, _viewport.Width, _scrollOffset.X),
                RevealAxis(target.Top, target.Bottom, _viewport.Height, _scrollOffset.Y, alignTop)), ViewportExtent(layout));
            if (offset != _scrollOffset)
            {
                _scrollOffset = offset;
                OnScrolled();
            }
        }

        static double RevealAxis(double start, double end, double size, double current, bool alignStart = false) =>
            alignStart || start < current || end - start > size ? start : end > current + size ? end - size : current;
    }

    private async void Activate(IElement element, Field? nativeToggle = null)
    {
        var page = Page;
        var activation = page.ActivateAsync(element);
        if (nativeToggle is not null && ReferenceEquals(page, Page))
            SyncField(element, nativeToggle);
        await activation;
    }

    private void ArrangeFields(HtmlScrollFrame frame)
    {
        var visible = new HashSet<IElement>(ReferenceEqualityComparer.Instance);
        foreach (var item in frame.VisibleItems())
        {
            if (item.ControlBounds is not { } controlBounds)
                continue;
            var (rect, clip) = frame.Place(item, controlBounds);
            if (HtmlScrollFrame.Intersect(rect, clip).IsEmpty || !visible.Add(item.Element))
                continue;
            var field = GetField(item.Element);
            SyncField(item.Element, field);
            ArrangeField(field, rect, clip);
        }
        foreach (var (element, field) in _fields.ToArray())
        {
            if (visible.Contains(element)) continue;
            if (field.Control.IsFocused && Page.Document.Contains(element))
            {
                ArrangeField(field, default, default);
                continue;
            }
            RemoveField(element, field);
        }
    }

    private Field GetField(IElement element)
    {
        var kind = element.LocalName == "input" ? InputType(element) : element.LocalName;
        if (_fields.TryGetValue(element, out var existing))
        {
            if (existing.Kind == kind)
                return existing;
            RemoveField(element, existing);
        }
        Control control = kind switch
        {
            "checkbox" => new CheckBox(),
            "radio" => new RadioButton(),
            "password" => new PasswordBox(),
            "select" => new ComboBox { ChangeOnWheel = false },
            "textarea" => new MultiLineTextBox { Wrap = true },
            _ => new TextBox()
        };
        var field = new Field(kind, control, element);
        _fields.Add(element, field);
        control.IsTabStop = false;
        control.GotFocus += () => SetPageFocus(field.Element);
        control.LostFocus += () =>
        {
            CommitText(field.Element, field);
            if (!_movingFocus && ReferenceEquals(_focusedElement, field.Element))
                SetPageFocus(null);
        };
        switch (control)
        {
            case CheckBox check:
                check.CheckedChanged += _ => Toggle();
                break;
            case RadioButton radio:
                radio.GroupName = Guid.NewGuid().ToString(); // Radio grouping belongs to the DOM/form owner.
                radio.CheckedChanged += _ => Toggle();
                break;
            case ComboBox select:
                select.SelectionChanged += _ => SelectChanged(field.Element, field, select.SelectedIndex);
                break;
            case PasswordBox password:
                password.PasswordChanged += () => EditText(field.Element, field, password.Password);
                break;
            case TextBase text:
                text.TextChanged += value => EditText(field.Element, field, value);
                break;
        }
        if (control is SingleLineTextBase)
            control.KeyDown += e =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitText(field.Element, field);
                    e.Handled = true;
                    if (HtmlForms.FormOwner(field.Element) is { } form)
                    {
                        if (HtmlForms.DefaultSubmitter(form) is { } submitter)
                            Activate(submitter);
                        else
                            _ = Page.SubmitFormAsync(form);
                    }
                }
            };
        SyncField(element, field);
        Insert(0, field.Host); // Keep overlay scrollbars above native fields.
        return field;

        void Toggle()
        {
            if (!field.Syncing)
                Activate(field.Element, field);
        }
    }

    private void ArrangeField(Field field, Rect rect, Rect clip) => field.Host.Place(ToWindow(rect), ToWindow(clip));

    private void SyncField(IElement element, Field field)
    {
        field.Syncing = true;
        try
        {
            field.Control.IsEnabled = !IsDisabled(element);
            var value = ReadValue(element);
            if (field.Control is TextBase editor)
            {
                editor.IsReadOnly = element.HasAttribute("readonly");
                editor.Placeholder = element.GetAttribute("placeholder") ?? "";
                editor.MaxLength = int.TryParse(element.GetAttribute("maxlength"), out var max) && max >= 0
                    ? max : int.MaxValue;
            }
            switch (field.Control)
            {
                case TextBox text when text.Text != value: text.Text = value; break;
                case PasswordBox password when password.Password != value: password.Password = value; break;
                case MultiLineTextBox area when area.Text != value: area.Text = value; break;
                case CheckBox check: check.IsChecked = ((IHtmlInputElement)element).IsChecked; break;
                case RadioButton radio: radio.IsChecked = ((IHtmlInputElement)element).IsChecked; break;
                case ComboBox combo:
                    var select = (IHtmlSelectElement)element;
                    var all = select.Options.ToArray();
                    var enabled = all.Where(option => !option.IsDisabled &&
                        !(option.ParentElement?.LocalName == "optgroup" && option.ParentElement.HasAttribute("disabled"))).ToArray();
                    var labels = enabled.Select(OptionLabel).ToArray();
                    if (!enabled.SequenceEqual(field.Options) || !labels.SequenceEqual(field.Labels))
                    {
                        field.Options = enabled;
                        field.Labels = labels;
                        combo.ItemsSource = ItemsView.Create(labels);
                    }
                    var selected = select.SelectedIndex;
                    combo.SelectedIndex = selected >= 0 && selected < all.Length ? Array.IndexOf(enabled, all[selected]) : -1;
                    combo.Placeholder = selected >= 0 && selected < all.Length ? OptionLabel(all[selected]) : "";
                    break;
            }
        }
        finally { field.Syncing = false; }
    }

    private void EditText(IElement element, Field field, string value)
    {
        if (field.Syncing || ReadValue(element) == value)
            return;
        if (Page.IsRendererMirror)
        {
            field.TextChanged = true;
            Page.SendRendererInput(element, HtmlRendererInputKind.Text, value);
            return;
        }
        if (element is IHtmlInputElement input)
            input.Value = value;
        else if (element is IHtmlTextAreaElement area)
            area.Value = value;
        field.TextChanged = true;
        Page.MarkDirty();
        Page.DispatchEvent(element, "input");
        Page.PublishChanges();
    }

    private void CommitText(IElement element, Field field)
    {
        if (!field.TextChanged || !Page.Document.Contains(element))
            return;
        field.TextChanged = false;
        if (Page.IsRendererMirror)
            Page.SendRendererInput(element, HtmlRendererInputKind.Change);
        else Page.DispatchEvent(element, "change");
    }

    private void SelectChanged(IElement element, Field field, int index)
    {
        if (field.Syncing || index < 0 || index >= field.Options.Length)
            return;
        var select = (IHtmlSelectElement)element;
        var selected = Array.IndexOf(select.Options.ToArray(), field.Options[index]);
        if (selected < 0 || selected == select.SelectedIndex)
            return;
        if (Page.IsRendererMirror)
        {
            Page.SendRendererInput(element, HtmlRendererInputKind.Select, index: selected);
            return;
        }
        foreach (var option in select.Options)
            option.IsSelected = ReferenceEquals(option, field.Options[index]);
        Page.FormControlChanged(element);
    }

    private void RemoveField(IElement element, Field field)
    {
        _fields.Remove(element);
        Remove(field.Host);
        field.Host.Dispose();
    }

    private HtmlLayoutResult GetLayout(Size size)
    {
        var key = new ViewportLayoutKey(size, Page.Version, _focusedElement, _hoveredElement, _pressedElement);
        if (_viewportLayout is { } cached && key == _viewportKey) return cached;
        var (vertical, horizontal) = (false, false);
        while (true)
        {
            var width = Math.Max(0, size.Width - (vertical ? ScrollBarWidth : 0));
            var height = Math.Max(0, size.Height - (horizontal ? ScrollBarWidth : 0));
            var layout = _layout.Get(Page.Document, Page.Version, width, MeasureText, viewportHeight: height,
                externalStyleSheet: Page.GetExternalStyle, imageSize: MeasureImage,
                focusedElement: _focusedElement, hoveredElement: _hoveredElement, activeElement: _pressedElement,
                dark: Theme.IsDark);
            var needVertical = NeedsBar(layout.ViewportY, layout.Height - height);
            var needHorizontal = NeedsBar(layout.ViewportX, layout.Width - width);
            if (needVertical && !vertical || needHorizontal && !horizontal)
            {
                vertical |= needVertical;
                horizontal |= needHorizontal;
                continue;
            }
            _viewport = new(0, 0, width, height);
            // Keep the reserved space stable if a media query changes during gutter negotiation.
            (_verticalGutter, _horizontalGutter) = (needVertical, needHorizontal);
            _viewportKey = key;
            return _viewportLayout = layout;
        }

        static bool NeedsBar(HtmlOverflow overflow, double extent) => overflow == HtmlOverflow.Scroll ||
            overflow == HtmlOverflow.Auto && extent > 0.01;
    }

    private Point ViewportExtent(HtmlLayoutResult layout) =>
        new(Math.Max(0, layout.Width - _viewport.Width), Math.Max(0, layout.Height - _viewport.Height));
    private static Point Clamp(Point offset, Point extent) =>
        new(Math.Clamp(offset.X, 0, extent.X), Math.Clamp(offset.Y, 0, extent.Y));
    private HtmlScrollFrame CreateFrame(HtmlLayoutResult layout) => new(layout, _viewport,
        Clamp(_scrollOffset, ViewportExtent(layout)), GetElementOffset);
    private Rect ToWindow(Rect rect) => rect.Offset(Bounds.X, Bounds.Y);

    private static ScrollBar NewBar(Orientation orientation) => new()
        { Orientation = orientation, IsTabStop = false, Focusable = false, IsVisible = false };

    private void ArrangeBar(ScrollBar bar, bool show, Rect rect, double maximum, double viewport, double value,
        ClipHost? host = null, Rect clip = default)
    {
        show &= !rect.IsEmpty;
        bar.IsVisible = show;
        if (!show) return;
        bar.Maximum = maximum;
        bar.ViewportSize = viewport;
        bar.SmallChange = WheelStep;
        bar.LargeChange = viewport;
        bar.Value = value;
        if (host is not null)
            host.Place(ToWindow(rect), ToWindow(clip));
        else
        {
            bar.Measure(rect.Size);
            bar.Arrange(ToWindow(rect));
        }
    }

    private void ArrangeElementBars(HtmlLayoutResult layout, HtmlScrollFrame frame)
    {
        var visible = new HashSet<object>();
        foreach (var region in layout.ScrollRegions)
        {
            if (!region.BarX && !region.BarY) continue;
            var (port, clip) = frame.RegionBounds(region);
            if (HtmlScrollFrame.Intersect(port, clip).IsEmpty) continue;
            var key = ScrollKey(region.Element);
            visible.Add(key);
            if (!_elementBars.TryGetValue(key, out var bars))
            {
                bars = new(region);
                _elementBars.Add(key, bars);
                var pair = bars;
                bars.Horizontal.ValueChanged += value => SetBarOffset(pair, value, horizontal: true);
                bars.Vertical.ValueChanged += value => SetBarOffset(pair, value, horizontal: false);
                Insert(Count - 2, bars.HorizontalHost);
                Insert(Count - 2, bars.VerticalHost);
            }
            bars.Region = region;
            bars.HorizontalHost.BarRegion = region;
            bars.VerticalHost.BarRegion = region;
            var offset = region.Clamp(GetElementOffset(region.Element));
            var gutterX = region.BarY ? Math.Min(ScrollBarWidth, port.Width) : 0;
            var gutterY = region.BarX ? Math.Min(ScrollBarWidth, port.Height) : 0;
            ArrangeBar(bars.Horizontal, region.BarX, new(port.Left, port.Bottom - gutterY, port.Width - gutterX, gutterY),
                region.MaxX, port.Width, offset.X, bars.HorizontalHost, clip);
            ArrangeBar(bars.Vertical, region.BarY, new(port.Right - gutterX, port.Top, gutterX, port.Height - gutterY),
                region.MaxY, port.Height, offset.Y, bars.VerticalHost, clip);
        }
        foreach (var (key, bars) in _elementBars.ToArray())
            if (!visible.Contains(key)) RemoveElementBars(key, bars);
    }

    private void SetBarOffset(ElementBars bars, double value, bool horizontal)
    {
        if (_syncingScrollBar) return;
        var offset = GetElementOffset(bars.Region.Element);
        _keyboardScrollTarget = bars.Region.Element;
        ScrollElement(bars.Region, horizontal ? new(value, offset.Y) : new(offset.X, value));
    }

    private void RemoveElementBars(object key, ElementBars bars)
    {
        _elementBars.Remove(key);
        Remove(bars.HorizontalHost);
        Remove(bars.VerticalHost);
        bars.HorizontalHost.Dispose();
        bars.VerticalHost.Dispose();
    }

    private Size? MeasureImage(IElement element) => GetPageImage(element)?.Source is { } source
        ? new Size(source.PixelWidth, source.PixelHeight) : null;

    private IImage? GetImage(IElement element)
    {
        var entry = GetPageImage(element);
        if (entry?.Source is not { } source)
            return null;
        if (entry.Image is null)
        {
            try { entry.Image = source.CreateImage(GetGraphicsFactory()); }
            catch (Exception error) when (IsDecodeError(error)) { entry.Dispose(); }
        }
        return entry.Image;
    }

    private PageImage? GetPageImage(IElement element)
    {
        var canvas = HtmlCanvas.IsCanvas(element);
        var pixels = Page.GetPixels(element) ?? (canvas ? Page.GetCanvasPixels(element) : null);
        var bytes = pixels?.Rgba ?? Page.GetImage(element);
        if (canvas)
        {
            if (_canvasSources.TryGetValue(element, out var old) && !ReferenceEquals(old, bytes))
                RemoveCanvasImage(element, old);
            if (bytes is not null) _canvasSources[element] = bytes;
        }
        if (bytes is null) return null;
        if (_images.TryGetValue(bytes, out var cached))
            return cached;
        var entry = new PageImage();
        _images.Add(bytes, entry);
        try
        {
            pixels ??= HtmlImageDecoder.Decode(bytes);
            if (pixels is { Width: > 0, Height: > 0 } && (long)pixels.Width * pixels.Height <= MaxDecodedImagePixels)
                entry.Source = HtmlRendererProjection.CreateImageSource(pixels);
        }
        catch (Exception error) when (IsDecodeError(error)) { }
        return entry;
    }

    private void RemoveCanvasImage(IElement element, byte[] pixels)
    {
        _canvasSources.Remove(element);
        if (_images.Remove(pixels, out var entry)) entry.Dispose();
    }

    private static bool IsDecodeError(Exception error) => error is ArgumentException or IOException or
        FormatException or InvalidOperationException or NotSupportedException or System.Runtime.InteropServices.COMException;

    private Size MeasureText(string text, HtmlTextStyle style) => GetTextLayout(text, style).MeasuredSize;

    private ITextLayout GetTextLayout(string text, HtmlTextStyle style)
    {
        var runStyle = new TextRunStyle(style.FontFamily ?? Theme.Metrics.FontFamily, style.FontSize,
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
        if (_browser is not null)
        {
            _browser.Changed += OnBrowserChanged;
            OnBrowserChanged();
        }
        OnPageChanged();
    }

    private void OnBrowserChanged()
    {
        if (_browser is not { } browser)
            return;
        var nextPage = browser.Page ?? _startPage!;
        if (!ReferenceEquals(nextPage, Page))
        {
            Page.Changed -= OnPageChanged;
            ResetPageVisuals();
            Page = nextPage;
            Page.Changed += OnPageChanged;
            OnPageChanged();
        }
        if (browser.NavigationVersion != _navigationVersion)
        {
            _navigationVersion = browser.NavigationVersion;
            _scrollOffset = new(browser.ScrollOffsetX, browser.ScrollOffset);
            _fragment = browser.ScrollFragment;
            _restoreScroll = true;
            RefreshLayout();
        }
    }

    private void OnPageChanged()
    {
        if (Page.IsRendererMirror)
        {
            foreach (var entry in _images.Values) entry.Dispose();
            _images.Clear();
            _canvasSources.Clear();
            _pressedElement = Rebind(_pressedElement);
            _keyboardScrollTarget = Rebind(_keyboardScrollTarget);
            _hoveredElement = Rebind(_hoveredElement);
            _focusedElement = Rebind(_focusedElement);
            foreach (var (previous, field) in _fields.ToArray())
            {
                var next = Rebind(previous);
                if (next is null || next.LocalName != previous.LocalName ||
                    (next.LocalName == "input" && InputType(next) != field.Kind)) continue;
                _fields.Remove(previous);
                field.Element = next;
                _fields.Add(next, field);
            }
            ClearLayout();
        }
        else if (_canvasSources.Count > 0)
        {
            foreach (var (element, bytes) in _canvasSources.ToArray())
                if (!Page.Document.Contains(element)) RemoveCanvasImage(element, bytes);
        }
        if (_window is { } window)
        {
            SynchronizeTitle?.Invoke(window);
            if (!ReferenceEquals(_focusedElement, Page.FocusedElement) && window.IsActive)
            {
                var page = Page;
                var focused = page.FocusedElement;
                _focusedElement = focused;
                Application.Current.Dispatcher?.BeginInvoke(() =>
                {
                    if (_window != window || !ReferenceEquals(Page, page) ||
                        !ReferenceEquals(Page.FocusedElement, focused) || Bounds.IsEmpty)
                        return;
                    if (focused is null)
                    {
                        if (IsFocusWithin && !IsFocused)
                            FocusView(null);
                    }
                    else if (FindBounds(GetLayout(Bounds.Size), focused) is { } item)
                        FocusPageItem(focused, item);
                });
            }
        }
        PageChanged?.Invoke();
        RefreshLayout();

        IElement? Rebind(IElement? old) => old is null ? null : Page.FindRendererElement(Page.GetRendererId(old));
    }

    private void StopObservingWindow()
    {
        if (_window is { } window)
        {
            window.Loaded -= ObservePage;
            window.Closed -= StopObservingWindow;
            window.PreviewKeyDown -= PreviewKeyDown;
            _window = null;
        }
        if (_observingPage)
        {
            Page.Changed -= OnPageChanged;
            if (_browser is not null)
                _browser.Changed -= OnBrowserChanged;
            _observingPage = false;
        }
        ResetPageVisuals();
    }

    private void ResetPageVisuals()
    {
        _pressedElement = _hoveredElement = _focusedElement = _keyboardScrollTarget = null;
        _tabEntryReverse = null;
        _scrollOffset = default;
        _navigationVersion = -1;
        _restoreScroll = false;
        _fragment = null;
        _elementOffsets.Clear();
        foreach (var (key, bars) in _elementBars.ToArray())
            RemoveElementBars(key, bars);
        foreach (var (element, field) in _fields.ToArray())
            RemoveField(element, field);
        foreach (var image in _images.Values)
            image.Dispose();
        _images.Clear();
        _canvasSources.Clear();
        ClearLayout();
    }

    private void RefreshLayout()
    {
        InvalidateArrange();
        InvalidateVisual();
    }

    private void ClearLayout()
    {
        _layout.Clear();
        _viewportLayout = null;
        _textLayouts.Clear();
    }

    private static string OptionLabel(IHtmlOptionElement option) => option.Label ?? option.Text;
    private static string InputType(IElement element) => (element.GetAttribute("type") ?? "text").ToLowerInvariant();
    private static int TabIndexOf(IElement element) => int.TryParse(element.GetAttribute("tabindex"), out var index) ? index : 0;
    private static bool IsDisabled(IElement element) => HtmlForms.IsDisabled(element);
    private static bool IsFocusable(IElement element) => !IsDisabled(element) && (element.LocalName switch
    {
        "a" => element.HasAttribute("href") || element.HasAttribute("tabindex"),
        "input" => InputType(element) != "hidden",
        "textarea" or "select" or "button" => true,
        "summary" => HtmlPage.IsDisclosureSummary(element) || element.HasAttribute("tabindex"),
        _ => element.HasAttribute("tabindex")
    });
    private static string ReadValue(IElement element) => element switch
    {
        IHtmlInputElement input => input.Value,
        IHtmlTextAreaElement area => area.Value,
        _ => ""
    };

    private static IElement? Ancestor(IElement? element, Func<IElement, bool> match)
    {
        for (; element is not null; element = element.ParentElement)
            if (match(element))
                return element;
        return null;
    }

    private static HtmlDrawItem? FindBounds(HtmlLayoutResult layout, IElement element)
    {
        foreach (var item in layout.Items)
            if (ReferenceEquals(item.Element, element) || element.Contains(item.Element))
                return item;
        return null;
    }

    private sealed class Field(string kind, Control control, IElement element)
    {
        internal IElement Element { get; set; } = element;
        internal string Kind { get; } = kind;
        internal Control Control { get; } = control;
        internal ClipHost Host { get; } = new(control);
        internal bool Syncing;
        internal bool TextChanged;
        internal IHtmlOptionElement[] Options = [];
        internal string[] Labels = [];
    }

    // The host bounds form the paint and hit clip without shrinking the native child.
    internal sealed class ClipHost : Panel
    {
        private readonly Control _child;
        private Rect _childRect;
        internal HtmlScrollRegion? BarRegion { get; set; }

        protected override UIElement? OnHitTest(Point point) => Bounds.Contains(point) ? _child.HitTest(point) : null;

        internal ClipHost(Control child)
        {
            _child = child;
            ClipToBounds = true;
            Add(child);
        }

        internal void Place(Rect rect, Rect clip)
        {
            var visible = HtmlScrollFrame.Intersect(rect, clip);
            if (visible.IsEmpty)
                visible = rect = new(-100_000, -100_000, 1, 1);
            if (_childRect != rect)
            {
                _childRect = rect;
                InvalidateArrange();
            }
            _child.Measure(rect.Size);
            Measure(visible.Size);
            Arrange(visible);
        }

        protected override Size MeasureOverride(Size availableSize) => availableSize;

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(_childRect);
            return finalSize;
        }
    }

    private sealed class ElementBars(HtmlScrollRegion region)
    {
        internal HtmlScrollRegion Region = region;
        internal ScrollBar Horizontal { get; } = NewBar(Orientation.Horizontal);
        internal ScrollBar Vertical { get; } = NewBar(Orientation.Vertical);
        internal ClipHost HorizontalHost => _horizontalHost ??= new(Horizontal);
        internal ClipHost VerticalHost => _verticalHost ??= new(Vertical);
        private ClipHost? _horizontalHost, _verticalHost;
    }

    private sealed class PageImage : IDisposable
    {
        internal ImageSource? Source;
        internal IImage? Image;
        public void Dispose()
        {
            Image?.Dispose();
            Source?.Dispose();
            Source = null;
            Image = null;
        }
    }

    private readonly record struct ViewportLayoutKey(Size Size, long Version, IElement? Focus, IElement? Hover, IElement? Active);
    private readonly record struct TextKey(string Text, TextRunStyle Style, uint Dpi);
}
