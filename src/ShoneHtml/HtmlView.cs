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
    private const int MaxCachedTextLayouts = 2048;
    private const int MaxCachedTextLength = 256;
    private const int MaxDecodedImagePixels = 16_000_000;
    private readonly HtmlLayout _layout = new();
    private readonly Dictionary<TextKey, ITextLayout> _textLayouts = new();
    private readonly Dictionary<byte[], PageImage> _images = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IElement, Field> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly ScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, IsTabStop = false, Focusable = false };
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
    private double _scrollOffset;
    private long _navigationVersion = -1;
    private bool _restoreScroll;
    private string? _fragment;

    public HtmlView(HtmlPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        Page = page;
        Focusable = true;
        IsTabStop = true;
        ClipToBounds = true;
        _scrollBar.ValueChanged += offset =>
        {
            if (!_syncingScrollBar)
                ScrollTo(offset);
        };
        Add(_scrollBar);
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
        if (_window is null || finalSize.Width <= 0 || finalSize.Height <= 0)
            return finalSize;

        var layout = GetLayout(finalSize);
        var maxOffset = Math.Max(0, layout.Height - finalSize.Height);
        if (_restoreScroll)
        {
            _restoreScroll = false;
            if (_fragment is { Length: > 0 } fragment &&
                (Page.Document.GetElementById(fragment) ?? Page.Document.QuerySelectorAll("a[name]")
                    .FirstOrDefault(element => element.GetAttribute("name") == fragment)) is { } target &&
                FindBounds(layout, target) is { Fixed: false } item)
                _scrollOffset = item.Bounds.Top;
            _fragment = null;
        }
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxOffset);

        _syncingScrollBar = true;
        try
        {
            _scrollBar.IsVisible = maxOffset > 0;
            _scrollBar.Maximum = maxOffset;
            _scrollBar.ViewportSize = finalSize.Height;
            _scrollBar.SmallChange = WheelStep;
            _scrollBar.LargeChange = finalSize.Height;
            _scrollBar.Value = _scrollOffset;
            var barSize = new Size(Math.Min(finalSize.Width, ScrollBarWidth), finalSize.Height);
            _scrollBar.Measure(barSize);
            _scrollBar.Arrange(new Rect(Bounds.Right - barSize.Width, Bounds.Y, barSize.Width, barSize.Height));
        }
        finally
        {
            _syncingScrollBar = false;
        }

        ArrangeFields(layout, GetPageBounds(finalSize));
        _browser?.UpdateScroll(_scrollOffset);
        return finalSize;
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
        _scrollBar.Dispose();
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
        if (oldTheme?.Metrics.FontFamily != newTheme.Metrics.FontFamily)
        {
            ClearLayout();
            RefreshLayout();
        }
    }

    protected override void OnRender(IGraphicsContext context)
    {
        base.OnRender(context);
        if (_window is null || Bounds.IsEmpty)
            return;

        var viewport = GetPageBounds(Bounds.Size);
        var layout = GetLayout(Bounds.Size);
        context.FillRectangle(Bounds, Color.White);
        context.Save();
        try
        {
            context.IntersectClip(viewport);
            foreach (var item in layout.GetVisibleItems(_scrollOffset, _scrollOffset + Bounds.Height, _scrollOffset))
            {
                var rect = InViewport(item.Bounds, item.Fixed);
                if (!rect.IntersectsWith(viewport))
                    continue;

                if (item.Background is { A: > 0 } background)
                    context.FillRectangle(rect, background);
                if (item.BorderWidth > 0 && item.BorderColor.A > 0)
                    context.DrawRectangle(rect, item.BorderColor, item.BorderWidth, strokeInset: true);
                if (item.ImageBounds is { } imageBounds && GetImage(item.Element) is { } image)
                    context.DrawImage(image, InViewport(imageBounds, item.Fixed));
                if (item.Text is { Length: > 0 } text)
                    context.Text.Draw(GetTextLayout(text, item.TextStyle), rect.TopLeft,
                        new TextDrawOptions(item.TextStyle.Color));
                if (item.ControlBounds is null && ReferenceEquals(_focusedElement, item.Element) && IsFocused)
                    context.DrawRectangle(rect, Theme.Palette.Accent, 1, strokeInset: true);
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
        if (!e.Handled && e.Delta.Y != 0)
            e.Handled = ScrollTo(_scrollOffset - e.Delta.Y * WheelStep);
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
        var offset = e.Key switch
        {
            Key.Up => _scrollOffset - WheelStep,
            Key.Down => _scrollOffset + WheelStep,
            Key.PageUp => _scrollOffset - Bounds.Height,
            Key.PageDown or Key.Space => _scrollOffset + (e.ShiftKey ? -Bounds.Height : Bounds.Height),
            Key.Home => 0,
            Key.End => double.MaxValue,
            _ => _scrollOffset
        };
        e.Handled = ScrollTo(offset);
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
        if (!item.Fixed)
        {
            var rect = item.ControlBounds ?? item.Bounds;
            if (rect.Top < _scrollOffset || rect.Height > Bounds.Height)
                ScrollTo(rect.Top);
            else if (rect.Bottom > _scrollOffset + Bounds.Height)
                ScrollTo(rect.Bottom - Bounds.Height);
        }
        if (item.ControlBounds is { } controlBounds)
        {
            // An offscreen input can enter the tab sequence before the next frame arranges it.
            var field = GetField(target);
            ArrangeField(field, InViewport(controlBounds, item.Fixed));
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
        FocusView(Ancestor(_pressedElement, IsFocusable));
        RefreshLayout();
        e.Handled = _pressedElement is not null;
    }

    private IElement? HitTestPage(MouseEventArgs e)
    {
        var point = e.GetPosition(this);
        return _window is not null && new Rect(GetPageBounds(Bounds.Size).Size).Contains(point)
            ? GetLayout(Bounds.Size).HitTest(point.X, point.Y + _scrollOffset, _scrollOffset) : null;
    }

    private bool ScrollTo(double offset)
    {
        if (_window is null || Bounds.IsEmpty)
            return false;
        offset = Math.Clamp(offset, 0, Math.Max(0, GetLayout(Bounds.Size).Height - Bounds.Height));
        if (offset == _scrollOffset)
            return false;
        _scrollOffset = offset;
        _pressedElement = null;
        _browser?.UpdateScroll(_scrollOffset);
        RefreshLayout();
        return true;
    }

    private async void Activate(IElement element, Field? nativeToggle = null)
    {
        var page = Page;
        var activation = page.ActivateAsync(element);
        if (nativeToggle is not null && ReferenceEquals(page, Page))
            SyncField(element, nativeToggle);
        await activation;
    }

    private void ArrangeFields(HtmlLayoutResult layout, Rect viewport)
    {
        var visible = new HashSet<IElement>(ReferenceEqualityComparer.Instance);
        foreach (var item in layout.GetVisibleItems(_scrollOffset, _scrollOffset + viewport.Height, _scrollOffset))
        {
            if (item.ControlBounds is not { } controlBounds)
                continue;
            var rect = InViewport(controlBounds, item.Fixed);
            if (!rect.IntersectsWith(viewport) || !visible.Add(item.Element))
                continue;
            var field = GetField(item.Element);
            SyncField(item.Element, field);
            ArrangeField(field, rect);
        }
        foreach (var (element, field) in _fields.ToArray())
        {
            if (visible.Contains(element))
                continue;
            if (field.Control.IsFocused && Page.Document.Contains(element))
            {
                field.Control.Arrange(new Rect(Bounds.X - 10_000, Bounds.Y - 10_000, 1, 1));
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
        var field = new Field(kind, control);
        _fields.Add(element, field);
        control.IsTabStop = false;
        control.GotFocus += () => SetPageFocus(element);
        control.LostFocus += () =>
        {
            CommitText(element, field);
            if (!_movingFocus && ReferenceEquals(_focusedElement, element))
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
                select.SelectionChanged += _ => SelectChanged(element, field, select.SelectedIndex);
                break;
            case PasswordBox password:
                password.PasswordChanged += () => EditText(element, field, password.Password);
                break;
            case TextBase text:
                text.TextChanged += value => EditText(element, field, value);
                break;
        }
        if (control is SingleLineTextBase)
            control.KeyDown += e =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitText(element, field);
                    e.Handled = true;
                    if (HtmlForms.FormOwner(element) is { } form)
                    {
                        if (HtmlForms.DefaultSubmitter(form) is { } submitter)
                            Activate(submitter);
                        else
                            _ = Page.SubmitFormAsync(form);
                    }
                }
            };
        SyncField(element, field);
        Insert(Count - 1, control); // Keep the scrollbar above page content.
        return field;

        void Toggle()
        {
            if (!field.Syncing)
                Activate(element, field);
        }
    }

    private static void ArrangeField(Field field, Rect rect)
    {
        field.Control.Measure(rect.Size);
        field.Control.Arrange(rect);
    }

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
        Page.DispatchEvent(element, "change");
    }

    private void SelectChanged(IElement element, Field field, int index)
    {
        if (field.Syncing || index < 0 || index >= field.Options.Length)
            return;
        var select = (IHtmlSelectElement)element;
        var selected = Array.IndexOf(select.Options.ToArray(), field.Options[index]);
        if (selected < 0 || selected == select.SelectedIndex)
            return;
        foreach (var option in select.Options)
            option.IsSelected = ReferenceEquals(option, field.Options[index]);
        Page.FormControlChanged(element);
    }

    private void RemoveField(IElement element, Field field)
    {
        _fields.Remove(element);
        Remove(field.Control);
        field.Control.Dispose();
    }

    private HtmlLayoutResult GetLayout(Size size) => _layout.Get(Page.Document, Page.Version,
        GetPageBounds(size).Width, MeasureText, viewportHeight: size.Height,
        externalStyleSheet: Page.GetExternalStyle, imageSize: MeasureImage,
        focusedElement: _focusedElement, hoveredElement: _hoveredElement, activeElement: _pressedElement);

    private Rect GetPageBounds(Size size) => new(Bounds.X, Bounds.Y, Math.Max(0, size.Width - ScrollBarWidth), size.Height);
    private Rect InViewport(Rect rect, bool fixedPosition) => rect.Offset(Bounds.X, Bounds.Y - (fixedPosition ? 0 : _scrollOffset));

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
        if (Page.GetImage(element) is not { } bytes)
            return null;
        if (_images.TryGetValue(bytes, out var cached))
            return cached;
        var entry = new PageImage();
        _images.Add(bytes, entry);
        try
        {
            var source = ImageSource.FromBytes(bytes);
            if (source.PixelWidth > 0 && source.PixelHeight > 0 &&
                (long)source.PixelWidth * source.PixelHeight <= MaxDecodedImagePixels)
                entry.Source = source;
            else
                source.Dispose();
        }
        catch (Exception error) when (IsDecodeError(error)) { }
        return entry;
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
            _scrollOffset = browser.ScrollOffset;
            _fragment = browser.ScrollFragment;
            _restoreScroll = true;
            RefreshLayout();
        }
    }

    private void OnPageChanged()
    {
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
        _pressedElement = _hoveredElement = _focusedElement = null;
        _tabEntryReverse = null;
        foreach (var (element, field) in _fields.ToArray())
            RemoveField(element, field);
        foreach (var image in _images.Values)
            image.Dispose();
        _images.Clear();
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

    private sealed class Field(string kind, Control control)
    {
        internal string Kind { get; } = kind;
        internal Control Control { get; } = control;
        internal bool Syncing;
        internal bool TextChanged;
        internal IHtmlOptionElement[] Options = [];
        internal string[] Labels = [];
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

    private readonly record struct TextKey(string Text, TextRunStyle Style, uint Dpi);
}
