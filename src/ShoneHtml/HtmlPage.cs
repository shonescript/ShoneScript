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
public sealed class HtmlPage : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<IElement, string> _styles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IElement, byte[]> _images = new(ReferenceEqualityComparer.Instance);
    private HtmlScriptHost? _scripts;
    private bool _dirty;
    private bool _disposed;
    private bool _executingDefault;

    private HtmlPage(IDocument document, HtmlBrowser? browser = null, Uri? address = null)
    {
        Document = document;
        Browser = browser;
        Address = address;
        Lifetime = _lifetime.Token;
        Context = browser?.Context ?? SynchronizationContext.Current;
    }

    public string Title => Document.Title ?? "";
    public Uri? Address { get; internal set; }
    public Uri? BaseAddress => Document.QuerySelector("base[href]")?.GetAttribute("href") is { } href &&
        Uri.TryCreate(Address, href, out var resolved) && HtmlResources.IsHttpAddress(resolved) ? resolved : Address;

    public Uri? ResolveAddress(string reference) => Uri.TryCreate(BaseAddress, reference.Trim(), out var result) &&
        HtmlResources.IsHttpAddress(result) ? result : null;

    internal IDocument Document { get; }
    internal HtmlBrowser? Browser { get; }
    internal SynchronizationContext? Context { get; }
    internal CancellationToken Lifetime { get; }
    internal string ResourceStatus { get; private set; } = "";
    internal IElement? FocusedElement { get; private set; }
    internal long Version { get; private set; }
    internal event Action? Changed;
    internal string? GetExternalStyle(IElement link) => _styles.GetValueOrDefault(link);
    internal byte[]? GetImage(IElement image) => _images.GetValueOrDefault(image);

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

    internal static async Task<HtmlPage> LoadDocumentAsync(HtmlBrowser browser, HtmlResource resource, CancellationToken token)
    {
        var mediaType = resource.ContentType?.Split(';', 2)[0].Trim().ToLowerInvariant();
        var document = await Task.Run(() =>
        {
            var html = mediaType is null or "text/html" or "application/xhtml+xml";
            var text = resource.DecodeText(html);
            return new HtmlParser().ParseDocument(html ? text : "<!doctype html><pre>" +
                System.Net.WebUtility.HtmlEncode(text) + "</pre>");
        }, token);
        token.ThrowIfCancellationRequested();
        ValidateTree(document, browser.Options, token);
        var page = new HtmlPage(document, browser, resource.Address);
        try
        {
            if (resource.IsSuccess)
                await page.LoadAssetsAsync(token);
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

    private async Task LoadAssetsAsync(CancellationToken token)
    {
        var options = Browser!.Options;
        var assets = Document.QuerySelectorAll("link[href], img[src]").Where(element => element.LocalName == "img" ||
            (element.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && !element.HasAttribute("disabled"));
        var candidates = assets.Take(options.MaximumPageResources).ToArray();
        var failures = assets.Skip(options.MaximumPageResources).Any() ? 1 : 0;
        var retainedBytes = 0L;
        var imagePixels = 0L;
        const int maximumPageImagePixels = 24_000_000;
        foreach (var group in candidates
            .Select(element =>
            {
                var reference = element.GetAttribute(element.LocalName == "img" ? "src" : "href");
                return (Element: element, Address: string.IsNullOrWhiteSpace(reference) ? null : ResolveAddress(reference));
            })
            .GroupBy(asset => asset.Address is { } uri ? HtmlResources.WithoutFragment(uri) : null).Chunk(4))
        {
            var results = await Task.WhenAll(group.Select(async assetsAtAddress =>
            {
                var address = assetsAtAddress.Key;
                try
                {
                    if (address is null)
                        return null;
                    return await Browser.Resources.LoadAsync(address, token);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    return null;
                }
            }));
            for (var index = 0; index < group.Length; index++)
            {
                var result = results[index];
                if (result is not { IsSuccess: true } || retainedBytes + result.Bytes.Length > options.MaximumPageResourceBytes)
                {
                    failures += group[index].Count();
                    continue;
                }
                retainedBytes += result.Bytes.Length;
                var pixels = HtmlRaster.GetPixelCount(result.Bytes);
                var showImage = pixels > 0 && imagePixels + pixels <= maximumPageImagePixels;
                var imageAdded = false;
                string? css = null;
                foreach (var (element, _) in group[index])
                {
                    if (element.LocalName == "img")
                    {
                        if (showImage)
                        {
                            _images[element] = result.Bytes;
                            imageAdded = true;
                        }
                        else
                            failures++;
                    }
                    else if (result.ContentType is null ||
                        string.Equals(result.ContentType.Split(';', 2)[0].Trim(), "text/css", StringComparison.OrdinalIgnoreCase))
                        _styles[element] = css ??= result.DecodeText();
                    else
                        failures++;
                }
                if (imageAdded)
                    imagePixels += pixels;
            }
        }
        if (failures > 0)
            ResourceStatus = $"{failures} page resource(s) unavailable.";
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

    internal void NotifyHistory(Uri previous, bool traversing) => _scripts?.NotifyHistory(previous, traversing);

    internal bool DispatchEvent(IElement node, string type, bool bubbles = true, bool cancelable = false) =>
        _scripts?.DispatchEvent(node, type, bubbles, cancelable) ?? true;

    internal void SetFocusedElement(IElement? element)
    {
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
        var control = target.Closest("a[href], button, input, label");
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
        if (control?.LocalName == "a" && ResolveAddress(control.GetAttribute("href")!) is { } address && Browser is { } browser)
            return browser.NavigateAsync(address);
        return Task.CompletedTask;
    }

    internal void FormControlChanged(IElement control)
    {
        DispatchEvent(control, "input");
        DispatchEvent(control, "change");
        MarkDirty();
        PublishChanges();
    }

    internal Task SubmitFormAsync(IElement form, IElement? submitter = null, bool dispatch = true)
    {
        if (Browser is null || dispatch && !DispatchEvent(form, "submit", cancelable: true))
            return Task.CompletedTask;
        return HtmlForms.CreateRequest(this, form, submitter) is { } request ? Browser.SubmitAsync(request) : Task.CompletedTask;
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
