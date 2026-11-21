using System.Net;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ShoneHtml;

/// <summary>Settings for application-supplied HTML and JavaScript.</summary>
public sealed class HtmlPageOptions
{
    public bool EnableScripts { get; init; } = true;
    public int MaximumLoopIterations { get; init; } = 1_000_000;
    public Action<HtmlScriptError>? OnScriptError { get; init; }
}

/// <summary>A failure in an inline script, event listener, or Promise job.</summary>
public sealed record HtmlScriptError(string Source, Exception Exception);

/// <summary>A parsed page and its JavaScript state. Access live pages on their owning thread.</summary>
public sealed partial class HtmlPage : IDisposable
{
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<IElement, string> _styles = new(ReferenceEqualityComparer.Instance);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IElement, HtmlImage> _images = new();
    private int _pageResourceCount;
    private long _pageResourceBytes;
    private long _pageImagePixels;
    private const int MaximumPageImagePixels = 24_000_000;
    private Dictionary<IElement, HtmlRendererPixels>? _pixels;
    private Dictionary<IElement, int>? _rendererIds;
    private Dictionary<int, IElement>? _rendererElements;
    private Dictionary<IElement, int>? _previousRendererIds;
    private HtmlRendererSession? _renderer;
    private readonly HtmlPublicNetwork.Scope? _publicScope;
    private HtmlScriptHost? _scripts;
    private bool _dirty;
    private bool _disposed;
    private bool _executingDefault;

    private HtmlPage(IDocument document, HtmlBrowser? browser = null, Uri? address = null,
        HtmlPublicNetwork.Scope? publicScope = null, HtmlRendererDocument? rendererContext = null,
        SynchronizationContext? context = null, CancellationToken rendererLifetime = default)
    {
        _lifetime = rendererLifetime.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(rendererLifetime) : new();
        Document = document;
        Browser = browser;
        Address = address;
        Lifetime = _lifetime.Token;
        Context = context ?? browser?.Context ?? SynchronizationContext.Current;
        _publicScope = publicScope;
        RendererContext = rendererContext;
    }

    public string Title => Document.Title ?? "";
    public Uri? Address { get; internal set; }
    public Uri? BaseAddress => Document.QuerySelector("base[href]")?.GetAttribute("href") is { } href &&
        Uri.TryCreate(Address, href, out var resolved) && HtmlResources.IsHttpAddress(resolved) ? resolved : Address;

    public Uri? ResolveAddress(string reference) => Uri.TryCreate(BaseAddress, reference.Trim(), out var result) &&
        HtmlResources.IsHttpAddress(result) ? result : null;

    internal IDocument Document { get; private set; }
    internal HtmlRendererDocument? RendererContext { get; }
    internal bool IsRendererMirror => _rendererIds is not null;
    internal HtmlBrowser? Browser { get; }
    internal SynchronizationContext? Context { get; }
    internal CancellationToken Lifetime { get; }
    internal string ResourceStatus { get; private set; } = "";
    internal IElement? FocusedElement { get; private set; }
    internal long Version { get; private set; }
    internal event Action? Changed;
    internal string? GetExternalStyle(IElement link) => _styles.GetValueOrDefault(link);
    internal byte[]? GetImage(IElement image) => _images.TryGetValue(image, out var state) ? state.Bytes : null;
    internal HtmlImage ImageState(IElement image) => _images.GetOrCreateValue(image);
    internal HtmlRendererPixels? GetPixels(IElement image) => _pixels?.GetValueOrDefault(image);
    internal int GetRendererId(IElement element)
    {
        if (_rendererIds?.TryGetValue(element, out var id) == true) return id;
        return _previousRendererIds?.GetValueOrDefault(element) ?? 0;
    }

    internal IElement? FindRendererElement(int id) => _rendererElements?.GetValueOrDefault(id);

    /// <summary>Parses HTML and runs its inline classic scripts in document order.</summary>
    public static HtmlPage Parse(string html, HtmlPageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlPageOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumLoopIterations);
        var page = new HtmlPage(new HtmlParser().ParseDocument(html));
        if (options.EnableScripts)
        {
            var scripts = page.Document.QuerySelectorAll("script");
            for (var index = 0; index < scripts.Length; index++)
            {
                if (!HtmlScriptHost.IsInlineClassicScript(scripts[index]))
                    continue;
                page._scripts ??= new HtmlScriptHost(page, options);
                page._scripts.RunInlineScript(scripts[index].TextContent, index + 1);
            }
            page._scripts?.FinishParsing();
            page.PublishChanges();
        }
        return page;
    }

    internal static Task<HtmlPage> LoadDocumentAsync(HtmlBrowser browser, HtmlResource resource, CancellationToken token,
        HtmlPublicNetwork.Scope? publicScope = null) => LoadDocumentCoreAsync(browser.Options, resource, token, browser, publicScope);

    internal static Task<HtmlPage> LoadRendererDocumentAsync(HtmlRendererDocument renderer, SynchronizationContext context,
        HtmlResource resource, CancellationToken token) => LoadDocumentCoreAsync(renderer.Options, resource, token, renderer: renderer, context: context);

    private static async Task<HtmlPage> LoadDocumentCoreAsync(HtmlBrowserOptions options, HtmlResource resource, CancellationToken token,
        HtmlBrowser? browser = null, HtmlPublicNetwork.Scope? publicScope = null, HtmlRendererDocument? renderer = null,
        SynchronizationContext? context = null)
    {
        var mediaType = resource.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var imageResponse = mediaType?.StartsWith("image/", StringComparison.Ordinal) == true;
        var directImage = imageResponse && publicScope is null && resource.IsSuccess && resource.Bytes.Length <= options.MaximumPageResourceBytes
            ? await Task.Run(() => HtmlImageDecoder.Decode(resource.Bytes, token), token) : null;
        var document = await Task.Run(() =>
        {
            var parser = new HtmlParser();
            if (imageResponse)
                return parser.ParseDocument(directImage is not null
                    ? "<!doctype html><title>Image</title><img alt='Image unavailable' style='display:block;max-width:100%;height:auto'>"
                    : "<!doctype html><title>Image unavailable</title><p>Unsupported or invalid image.</p>");
            var html = mediaType is null or "text/html" or "application/xhtml+xml";
            var text = resource.DecodeText(html);
            return parser.ParseDocument(html ? text : "<!doctype html><pre>" + WebUtility.HtmlEncode(text) + "</pre>");
        }, token);
        token.ThrowIfCancellationRequested();
        ValidateTree(document, options, token);
        var page = new HtmlPage(document, browser, resource.Address, publicScope, renderer, context,
            renderer is null ? default : token);
        try
        {
            if (directImage is not null)
            {
                page._pageResourceCount = 1;
                page._pageResourceBytes = resource.Bytes.Length;
                page._pageImagePixels = (long)directImage.Width * directImage.Height;
                page.ImageState(document.QuerySelector("img")!).Load(resource.Bytes, directImage, resource.Address.AbsoluteUri);
            }
            else if (imageResponse)
                page.ResourceStatus = "Unsupported or invalid image.";
            else if (resource.IsSuccess)
                await page.LoadAssetsAsync(options, token);
            return page;
        }
        catch
        {
            page.Dispose();
            throw;
        }
    }

    private static void ValidateTree(IDocument document, HtmlBrowserOptions options, CancellationToken token)
    {
        var nodes = new Stack<(INode Node, int Depth)>();
        nodes.Push((document, 0));
        var count = 0;
        while (nodes.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++count > options.MaximumDocumentNodes || item.Depth > options.MaximumDocumentDepth ||
                nodes.Count + item.Node.ChildNodes.Length > options.MaximumDocumentNodes)
                throw new InvalidDataException("Document exceeds the configured DOM size or depth limit.");
            foreach (var child in item.Node.ChildNodes)
                nodes.Push((child, item.Depth + 1));
        }
    }

    private async Task LoadAssetsAsync(HtmlBrowserOptions options, CancellationToken token)
    {
        var assets = Document.QuerySelectorAll("link[href], img[src]").Where(element => element.LocalName == "img" ||
            (element.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && !element.HasAttribute("disabled"));
        var candidates = assets.Take(options.MaximumPageResources).ToArray();
        var failures = assets.Skip(options.MaximumPageResources).Any() ? 1 : 0;
        _pageResourceCount = candidates.Length;
        foreach (var group in candidates
            .Select(element =>
            {
                var reference = element.GetAttribute(element.LocalName == "img" ? "src" : "href");
                var data = element.LocalName == "img" && reference?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true
                    ? reference : null;
                var address = data is null && !string.IsNullOrWhiteSpace(reference) ? ResolveAddress(reference) : null;
                return (Element: element, Key: (Reference: data ?? (address is null ? null : HtmlResources.WithoutFragment(address).AbsoluteUri),
                    Kind: element.LocalName == "img" ? HtmlPublicRequestKind.Image : HtmlPublicRequestKind.Style));
            })
            .GroupBy(asset => asset.Key).Chunk(4))
        {
            var results = await Task.WhenAll(group.Select(async assetsAtAddress =>
            {
                var (reference, kind) = assetsAtAddress.Key;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (kind == HtmlPublicRequestKind.Image)
                        return await LoadImageResourceAsync(reference, options.MaximumResponseBytes, token);
                    if (reference is null)
                        return null;
                    if (RendererContext is { } renderer)
                        return await renderer.LoadAsync(new(kind, reference, "GET", null, null), token);
                    if (_publicScope is { } scope)
                        return await scope.LoadAsync(new Uri(reference), token, kind);
                    return await Browser!.Resources.LoadAsync(new Uri(reference), token);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    return null;
                }
            }));
            for (var index = 0; index < group.Length; index++)
            {
                var result = results[index];
                if (result is not { IsSuccess: true } || !CountResourceBytes(result.Bytes.Length, options.MaximumPageResourceBytes))
                {
                    failures += group[index].Count();
                    continue;
                }
                var image = group[index].Key.Kind == HtmlPublicRequestKind.Image ? DecodePageImage(result.Bytes, token) : null;
                string? css = null;
                foreach (var (element, _) in group[index])
                {
                    if (element.LocalName == "img")
                    {
                        if (image is not null)
                            ImageState(element).Load(result.Bytes, image, group[index].Key.Reference!);
                        else
                            failures++;
                    }
                    else if (result.ContentType is null && _publicScope is null && RendererContext is null ||
                        string.Equals(result.ContentType?.Split(';', 2)[0].Trim(), "text/css", StringComparison.OrdinalIgnoreCase))
                        _styles[element] = css ??= result.DecodeText();
                    else
                        failures++;
                }
            }
        }
        if (failures > 0)
            ResourceStatus = $"{failures} page resource(s) unavailable.";
    }

    internal bool ReserveImageRequest(int limit)
    {
        if (_pageResourceCount >= limit) return false;
        _pageResourceCount++;
        return true;
    }

    internal bool CountResourceBytes(int bytes, int limit)
    {
        if (_pageResourceBytes + bytes > limit) return false;
        _pageResourceBytes += bytes;
        return true;
    }

    internal HtmlRendererPixels? DecodePageImage(byte[] bytes, CancellationToken token)
    {
        var pixels = HtmlRaster.GetPixelCount(bytes);
        if (pixels == 0 || _pageImagePixels + pixels > MaximumPageImagePixels) return null;
        _pageImagePixels += pixels;
        return HtmlImageDecoder.Decode(bytes, token);
    }

    internal async Task<HtmlResource?> LoadImageResourceAsync(string? reference, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (reference is null || _publicScope is not null) return null;
        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DecodeEmbeddedImage(reference, limit);
        if (RendererContext is { } renderer)
            return reference.Length <= 8192
                ? await renderer.LoadAsync(new(HtmlPublicRequestKind.Image, reference, "GET", null, null), token).ConfigureAwait(false)
                : null;
        return Browser is { } browser
            ? await browser.Resources.LoadAsync(new Uri(reference), token).ConfigureAwait(false)
            : null;
    }

    private static HtmlResource? DecodeEmbeddedImage(string reference, int limit)
    {
        var comma = reference.IndexOf(',');
        const string base64 = ";base64";
        if (comma < 5 + base64.Length || !reference.AsSpan(5, comma - 5).EndsWith(base64, StringComparison.OrdinalIgnoreCase))
            return null;
        var media = reference[5..(comma - base64.Length)].ToLowerInvariant();
        if (media is not ("image/png" or "image/jpeg" or "image/gif" or "image/bmp" or "image/webp"))
            return null;
        var payload = reference.AsSpan(comma + 1);
        if (payload.Length > ((long)limit + 2) / 3 * 4)
            return null;
        var bytes = new byte[(int)((long)payload.Length / 4 * 3)];
        if (!Convert.TryFromBase64Chars(payload, bytes, out var written) || written > limit)
            return null;
        return new HtmlResource(new Uri("about:blank"), HttpStatusCode.OK, bytes.AsSpan(0, written).ToArray(), media,
            new Dictionary<string, string>(), false);
    }

    internal async Task LoadScriptsAsync(HtmlPageOptions options)
    {
        _scripts = new HtmlScriptHost(this, new HtmlPageOptions
        {
            MaximumLoopIterations = options.MaximumLoopIterations,
            OnScriptError = error =>
            {
                Browser?.ReportScriptError(this, error);
                options.OnScriptError?.Invoke(error);
            }
        });
        await _scripts.StartLoading();
    }

    public void PumpTasks() => _scripts?.PumpTasks();

    internal void NotifyHistory(Uri previous, bool traversing)
    {
        if (_renderer is { } renderer && Address is { } address)
            renderer.Send(new(HtmlRendererInputKind.Address, Text: address.AbsoluteUri, Index: traversing ? 1 : 0));
        else _scripts?.NotifyHistory(previous, traversing);
    }

    internal static HtmlPage CreateRendererMirror(HtmlBrowser browser, Uri address) =>
        new(new HtmlParser().ParseDocument(""), browser, address);

    internal void AttachRenderer(HtmlRendererSession renderer) => _renderer = renderer;

    internal void ApplyRendererSnapshot(HtmlRendererSnapshot snapshot, int focusedId)
    {
        var mirror = HtmlRendererProjection.Materialize(snapshot);
        Document = mirror.Document;
        _previousRendererIds = _rendererIds;
        _rendererIds = mirror.Ids;
        _rendererElements = mirror.Ids.ToDictionary(pair => pair.Value, pair => pair.Key);
        _pixels = mirror.Images;
        FocusedElement = FindRendererElement(focusedId);
        _styles.Clear();
        foreach (var (element, css) in mirror.Styles) _styles.Add(element, css);
        _images.Clear();
        MarkDirty();
        try { PublishChanges(); }
        finally { _previousRendererIds = null; }
    }

    internal void SendRendererInput(IElement? node, HtmlRendererInputKind kind, string? text = null, int index = -1)
    {
        if (_renderer is not { } renderer || _rendererIds is not { } ids) return;
        if (node is null) renderer.Send(new(kind, Text: text, Index: index));
        else if (ids.TryGetValue(node, out var id)) renderer.Send(new(kind, id, text, index));
    }

    internal bool DispatchEvent(IElement node, string type, bool bubbles = true, bool cancelable = false) =>
        _scripts?.DispatchEvent(node, type, bubbles, cancelable) ?? true;

    internal void SetFocusedElement(IElement? element)
    {
        if (IsRendererMirror) { SendRendererInput(element, HtmlRendererInputKind.Focus); return; }
        if (ReferenceEquals(FocusedElement, element))
            return;
        var previous = FocusedElement;
        FocusedElement = element;
        if (previous is not null)
        {
            DispatchEvent(previous, "blur", bubbles: false);
            DispatchEvent(previous, "focusout");
        }
        if (element is not null)
        {
            DispatchEvent(element, "focus", bubbles: false);
            DispatchEvent(element, "focusin");
        }
        MarkDirty();
        PublishChanges();
    }

    internal void Click(IElement target) => _ = ActivateAsync(target);

    internal Task ActivateAsync(IElement target) => Browser is { } browser
        ? browser.RunAsync(() => ActivateCore(target)) : ActivateCore(target);

    private Task ActivateCore(IElement target)
    {
        if (_disposed || !Document.Contains(target))
            return Task.CompletedTask;
        if (IsRendererMirror)
        {
            SendRendererInput(target, HtmlRendererInputKind.Click);
            return Task.CompletedTask;
        }
        IElement? control = target;
        while (control is not null && !IsDisclosureSummary(control) &&
            !control.Matches("a[href], button, input, label, select, textarea"))
            control = control.ParentElement;
        if (control is { LocalName: "button" or "input" } && HtmlForms.IsDisabled(control))
            return Task.CompletedTask;

        IHtmlInputElement? checkable = null;
        IHtmlInputElement? previousRadio = null;
        var wasChecked = false;
        if (control is IHtmlInputElement input && input.Type.ToLowerInvariant() is "checkbox" or "radio")
        {
            checkable = input;
            wasChecked = input.IsChecked;
            if (input.Type.Equals("radio", StringComparison.OrdinalIgnoreCase))
            {
                previousRadio = HtmlForms.RadioGroup(input).OfType<IHtmlInputElement>()
                    .FirstOrDefault(other => other != input && other.IsChecked);
                if (previousRadio is not null)
                    previousRadio.IsChecked = false;
                input.IsChecked = true;
            }
            else
                input.IsChecked = !wasChecked;
        }

        if (!DispatchEvent(target, "click", cancelable: true))
        {
            if (checkable is not null)
                checkable.IsChecked = wasChecked;
            if (previousRadio is not null)
                previousRadio.IsChecked = true;
            if (checkable is not null)
            {
                MarkDirty();
                PublishChanges();
            }
            return Task.CompletedTask;
        }
        if (checkable is not null)
        {
            if (checkable.IsChecked != wasChecked)
                FormControlChanged(checkable);
            return Task.CompletedTask;
        }
        if (control is not null && IsDisclosureSummary(control) && control.ParentElement is { } details && Document.Contains(details))
        {
            if (details.HasAttribute("open")) details.RemoveAttribute("open");
            else details.SetAttribute("open", "");
            MarkDirty();
            PublishChanges();
            return Task.CompletedTask;
        }
        if (control?.LocalName == "label" && !_executingDefault)
        {
            var associated = control.GetAttribute("for") is { } id ? Document.GetElementById(id) :
                control.QuerySelector("input:not([type=hidden]), button, select, textarea");
            if (associated?.LocalName is "button" or "input" or "select" or "textarea" && !HtmlForms.IsDisabled(associated))
            {
                SetFocusedElement(associated);
                _executingDefault = true;
                try { return ActivateCore(associated); }
                finally { _executingDefault = false; }
            }
        }
        if (control is not null && HtmlForms.FormOwner(control) is { } form)
        {
            if (HtmlForms.IsSubmitter(control))
                return SubmitFormAsync(form, control);
            if (control.GetAttribute("type")?.ToLowerInvariant() == "reset" && control.LocalName is "button" or "input")
                ResetForm(form);
        }
        if (control?.LocalName == "a" && ResolveAddress(control.GetAttribute("href")!) is { } address)
        {
            if (RendererContext is { } renderer) renderer.Navigate(new(address.AbsoluteUri, "GET"));
            else if (Browser is { } browser) return browser.NavigateAsync(address);
        }
        return Task.CompletedTask;
    }

    internal static IElement? FirstSummary(IElement element)
    {
        if (element is IHtmlDetailsElement)
            foreach (var child in element.Children)
                if (child.LocalName == "summary" && child.NamespaceUri == NamespaceNames.HtmlUri)
                    return child;
        return null;
    }

    internal static bool IsDisclosureSummary(IElement element) => element.LocalName == "summary" &&
        element.ParentElement is { } parent && ReferenceEquals(FirstSummary(parent), element);

    internal void FormControlChanged(IElement control)
    {
        DispatchEvent(control, "input");
        DispatchEvent(control, "change");
        MarkDirty();
        PublishChanges();
    }

    internal Task SubmitFormAsync(IElement form, IElement? submitter = null, bool dispatch = true)
    {
        if (IsRendererMirror)
        {
            SendRendererInput(form, HtmlRendererInputKind.Submit);
            return Task.CompletedTask;
        }
        if (dispatch && !form.HasAttribute("novalidate") && submitter?.HasAttribute("formnovalidate") != true &&
            _scripts?.ValidateForm(form, report: true) == false)
            return Task.CompletedTask;
        if (Browser is null && RendererContext is null || dispatch && !DispatchEvent(form, "submit", cancelable: true) ||
            HtmlForms.CreateRequest(this, form, submitter) is not { } request)
            return Task.CompletedTask;
        if (RendererContext is { } renderer)
        {
            renderer.Navigate(new(request.Address.AbsoluteUri, request.Method.Method, request.Body, request.EncodedQuery));
            return Task.CompletedTask;
        }
        return Browser!.SubmitAsync(request);
    }

    internal void ResetForm(IElement form)
    {
        if (form is IHtmlFormElement htmlForm && DispatchEvent(form, "reset", cancelable: true))
        {
            htmlForm.Reset();
            MarkDirty();
            PublishChanges();
        }
    }

    internal void MarkDirty() => _dirty = true;

    internal void PublishChanges()
    {
        if (!_dirty || _disposed)
            return;
        _dirty = false;
        Version++;
        Changed?.Invoke();
    }

    internal void Stop()
    {
        if (!Lifetime.IsCancellationRequested)
            _lifetime.Cancel();
        _renderer?.Dispose();
        _renderer = null;
        _scripts?.Dispose();
        _scripts = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _lifetime.Dispose();
    }
}
