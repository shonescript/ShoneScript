using AngleSharp.Dom;
using ShoneJs;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private const int MaximumScripts = 256;
    private const int MaximumModuleBytes = 16 * 1024 * 1024;
    private TaskCompletionSource? _loadCompletion;
    private bool _domContentLoaded;
    private int _asyncScripts;

    public Task StartLoading()
    {
        if (_loadCompletion is { } pending) return pending.Task;
        var completion = _loadCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = _page.Lifetime.Register(() => completion.TrySetCanceled(_page.Lifetime));
        _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (completion.Task.IsCompleted) return completion.Task;
        Script[] scripts = Renderer is { } renderer && (renderer.HasCsp || _page.Document.QuerySelectorAll("meta[http-equiv]").Any(meta =>
            string.Equals(meta.GetAttribute("http-equiv")?.Trim(), "content-security-policy", StringComparison.OrdinalIgnoreCase)))
            ? [] : _page.Document.QuerySelectorAll("script").Select((element, index) => DescribeScript(element, index + 1))
                .OfType<Script>().Take(MaximumScripts + 1).ToArray();
        if (scripts.Length > MaximumScripts)
        {
            if (Renderer is not null) throw BudgetError("Page script limit exceeded.");
            _report?.Invoke(new HtmlScriptError("document", new InvalidOperationException("Page script limit exceeded.")));
            scripts = scripts[..MaximumScripts];
        }
        var deferred = scripts.Where(script => !script.Async && script.Deferred).ToArray();
        int parserIndex = 0, deferredIndex = 0;
        AdvanceParser();
        return completion.Task;

        void AdvanceParser()
        {
            while (parserIndex < scripts.Length && !_page.Lifetime.IsCancellationRequested)
            {
                var script = scripts[parserIndex++];
                if (script.Async)
                {
                    _asyncScripts++;
                    BeginScript(script, () => { _asyncScripts--; CompleteIfReady(); });
                }
                else if (!script.Deferred)
                {
                    if (script.External)
                    {
                        BeginScript(script, AdvanceParser);
                        return;
                    }
                    RunInlineScript(script.Text, script.Index);
                }
            }
            if (_page.Lifetime.IsCancellationRequested) return;
            SetReadyState("interactive");
            AdvanceDeferred();
        }

        void AdvanceDeferred()
        {
            if (_page.Lifetime.IsCancellationRequested) return;
            if (deferredIndex < deferred.Length) { BeginScript(deferred[deferredIndex++], AdvanceDeferred); return; }
            FireDomContentLoaded();
            CompleteIfReady();
        }
    }

    public void FinishParsing()
    {
        SetReadyState("interactive");
        FireDomContentLoaded();
        CompleteIfReady();
    }

    public void NotifyHistory(Uri previous, bool traversing)
    {
        if (traversing) DispatchWindowEvent("popstate");
        if (_page.Address is { } current && previous.Fragment != current.Fragment &&
            HtmlResources.WithoutFragment(previous) == HtmlResources.WithoutFragment(current))
            DispatchWindowEvent("hashchange", previous.AbsoluteUri, current.AbsoluteUri);
    }

    private void FireDomContentLoaded()
    {
        _domContentLoaded = true;
        Dispatch(GetWrapper(_page.Document), NewEvent("DOMContentLoaded", true, false));
        DrainJobs("DOMContentLoaded");
    }

    private void SetReadyState(string value)
    {
        _readyState = value;
        Dispatch(GetWrapper(_page.Document), NewEvent("readystatechange", false, false));
        DrainJobs("readystatechange");
    }

    private void CompleteIfReady()
    {
        if (!_domContentLoaded || _asyncScripts != 0 || _page.Lifetime.IsCancellationRequested || _readyState == "complete") return;
        SetReadyState("complete");
        Dispatch(_window, NewEvent("load", false, false));
        DrainJobs("load");
        _page.PublishChanges();
        _loadCompletion?.TrySetResult();
    }

    internal static bool IsExecutableScript(IElement element) =>
        element.NamespaceUri == "http://www.w3.org/1999/xhtml" &&
        (IsModule(element) || !element.HasAttribute("nomodule") && ClassicTypes.Contains(element.GetAttribute("type")?.Trim() ?? ""));

    internal static bool IsJavaScriptMediaType(string? contentType) =>
        contentType?.Split(';', 2)[0].Trim() is { Length: > 0 } media && ClassicTypes.Contains(media);

    private static bool IsModule(IElement element) =>
        string.Equals(element.GetAttribute("type")?.Trim(), "module", StringComparison.OrdinalIgnoreCase);

    private Script? DescribeScript(IElement element, int index)
    {
        if (!IsExecutableScript(element) || Renderer is not null && element.HasAttribute("integrity")) return null;
        var module = IsModule(element);
        var src = element.GetAttribute("src");
        var async = (module || src is not null) && element.HasAttribute("async");
        return new(element, index, module, src, element.TextContent, async, module || src is not null && element.HasAttribute("defer"));
    }

    private void BeginScript(Script script, Action done)
    {
        _ = LoadScriptAsync(script, done);
    }

    private async Task LoadScriptAsync(Script script, Action done)
    {
        var source = $"script[{script.Index}]";
        try
        {
            var address = script.External ? ScriptAddress(script) : null;
            if (address is not null) source += $" ({address.AbsoluteUri})";
            if (script.Module)
            {
                var graph = await DownloadGraphAsync(script).ConfigureAwait(false);
                if (!PostTask(source, () => EvaluateGraph(script, graph, done))) FailToSchedule();
            }
            else
            {
                var resource = await DownloadScriptAsync(address!, module: false).ConfigureAwait(false);
                source = $"script[{script.Index}] ({resource.Address.AbsoluteUri})";
                var text = resource.DecodeText();
                if (!PostTask(source, () =>
                {
                    var success = false;
                    Run(source, () => { _engine.Evaluate(text); success = true; });
                    DrainJobs(source);
                    FinishScript(script, done, success);
                })) FailToSchedule();
            }
        }
        catch (OperationCanceledException) when (_page.Lifetime.IsCancellationRequested) { }
        catch (RendererBudgetException) { }
        catch (InvalidOperationException error) when (Renderer is not null && IsEngineBudgetError(error))
        {
            TerminateRenderer(error.Message);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or FormatException or ArgumentException or ShoneJs.Parsing.JsSyntaxException)
        {
            if (!PostTask(source, () =>
            {
                _report?.Invoke(new HtmlScriptError(source, error));
                FinishScript(script, done, success: false);
            })) FailToSchedule();
        }
    }

    private void FailToSchedule()
    {
        if (!_page.Lifetime.IsCancellationRequested)
            _loadCompletion?.TrySetException(new InvalidOperationException("Page script task could not be enqueued."));
    }

    private void FinishScript(Script script, Action done, bool success)
    {
        if (_page.Lifetime.IsCancellationRequested) return;
        if (script.External || !success)
        {
            Dispatch(GetWrapper(script.Element), NewEvent(success ? "load" : "error", false, false));
            DrainJobs(success ? "script load" : "script error");
        }
        _page.PublishChanges();
        done();
    }

    private Uri ScriptAddress(Script script) => !string.IsNullOrWhiteSpace(script.Src) &&
        (Renderer is null || script.Src.Length <= 8192) && _page.ResolveAddress(script.Src) is { } address
        ? HtmlResources.WithoutFragment(address) : throw new HttpRequestException("Invalid script URL.");

    private async Task<HtmlResource> DownloadScriptAsync(Uri address, bool module)
    {
        var renderer = Renderer;
        var origin = renderer?.Origin ?? _page.Address;
        if (origin is null || !HtmlResources.IsHttpAddress(address) || !HtmlResources.SameOrigin(origin, address) ||
            renderer is not null && address.AbsoluteUri.Length > 8192)
            throw new HttpRequestException("Only same-origin HTTP(S) scripts are supported.");
        var resource = renderer is not null
            ? await renderer.LoadAsync(new(HtmlPublicRequestKind.Script, address.AbsoluteUri, "GET", null, null), _page.Lifetime).ConfigureAwait(false)
            : _page.Browser is { } browser
                ? await browser.Resources.LoadAsync(address, _page.Lifetime, allowRedirect: (_, next) => HtmlResources.SameOrigin(origin, next)).ConfigureAwait(false)
                : throw new HttpRequestException("Script loading requires a browser document.");
        if (renderer is not null && !HtmlResources.SameOrigin(origin, resource.Address))
            throw new HttpRequestException("Script redirect must stay within the document origin.");
        if (!resource.IsSuccess) throw new HttpRequestException($"Script request returned HTTP {(int)resource.Status}.");
        var media = resource.ContentType?.Split(';', 2)[0].Trim();
        if ((module || renderer is not null) && !IsJavaScriptMediaType(media))
            throw new HttpRequestException(module ? "Module response does not have a JavaScript media type." : "Script response does not have a JavaScript media type.");
        if (!module && renderer is null && (media?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ||
            media?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true || media?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ||
            media?.Equals("text/csv", StringComparison.OrdinalIgnoreCase) == true ||
            resource.Headers.TryGetValue("X-Content-Type-Options", out var options) && options.Equals("nosniff", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(media) || !ClassicTypes.Contains(media))))
            throw new HttpRequestException("Script response does not have an allowed media type.");
        return resource;
    }

    // Module downloads and parsing share a per-document cache. Only registration and evaluation touch the engine runtime.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Uri, Lazy<Task<ModuleSource>>> _moduleSources = new();
    private readonly HashSet<Uri> _registeredModules = [];

    private async Task<ModuleGraph> DownloadGraphAsync(Script root)
    {
        var sources = new Dictionary<Uri, ModuleEntry>();
        var rootRequest = root.External ? ScriptAddress(root) : new Uri($"about:shone-inline-module-{root.Index}");
        var requested = new Dictionary<Uri, Uri>();
        long bytes = 0;
        var key = await Visit(rootRequest, root.External ? null : root.Text).ConfigureAwait(false);
        return new ModuleGraph(key, sources);

        async Task<Uri> Visit(Uri url, string? inline = null)
        {
            if (requested.TryGetValue(url, out var key)) return key;
            if (requested.Count >= MaximumScripts)
            {
                if (Renderer is not null) throw BudgetError("Module dependency count exceeded.");
                throw new InvalidOperationException("Module dependency count exceeded.");
            }
            _page.Lifetime.ThrowIfCancellationRequested();
            var source = inline is null
                ? await _moduleSources.GetOrAdd(url, uri => new Lazy<Task<ModuleSource>>(() => DownloadModuleAsync(uri))).Value.ConfigureAwait(false)
                : new ModuleSource(url, _engine.ParseModule(inline, _page.Lifetime), inline.Length * 2L);
            requested.Add(url, source.Address);
            if (sources.ContainsKey(source.Address)) return source.Address;
            bytes += source.Size;
            if (bytes > MaximumModuleBytes)
            {
                if (Renderer is not null) throw BudgetError("Module graph size exceeded.");
                throw new InvalidOperationException("Module graph size exceeded.");
            }
            var baseAddress = inline is null ? source.Address : _page.BaseAddress;
            var map = new Dictionary<string, Uri>(StringComparer.Ordinal);
            sources.Add(source.Address, new ModuleEntry(source.Parsed, map));
            foreach (var specifier in source.Parsed.RequestedModules)
            {
                if (map.ContainsKey(specifier)) continue;
                if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal) &&
                    !specifier.StartsWith('/') && !Uri.TryCreate(specifier, UriKind.Absolute, out _))
                    throw new HttpRequestException($"Unsupported bare module specifier '{specifier}'.");
                if (!Uri.TryCreate(baseAddress, specifier, out var dependency) || !HtmlResources.IsHttpAddress(dependency))
                    throw new HttpRequestException($"Invalid module specifier '{specifier}'.");
                map.Add(specifier, await Visit(HtmlResources.WithoutFragment(dependency)).ConfigureAwait(false));
            }
            return source.Address;
        }
    }

    private async Task<ModuleSource> DownloadModuleAsync(Uri uri)
    {
        var resource = await DownloadScriptAsync(uri, module: true).ConfigureAwait(false);
        return new ModuleSource(HtmlResources.WithoutFragment(resource.Address), _engine.ParseModule(resource.DecodeUtf8(), _page.Lifetime), resource.Bytes.Length);
    }

    private void EvaluateGraph(Script script, ModuleGraph graph, Action done)
    {
        var source = script.External ? graph.Root.AbsoluteUri : $"script[{script.Index}]";
        var started = false;
        Run(source, () =>
        {
            foreach (var (id, entry) in graph.Sources)
                if (!_registeredModules.Contains(id))
                {
                    _engine.RegisterModule(id, entry.Parsed, entry.Resolutions);
                    _registeredModules.Add(id);
                }
            var promise = _engine.EvaluateModule(graph.Root, _page.Lifetime);
            var then = (promise.Data as JsObject)?.Get("then").Data as JsFunction ?? throw new InvalidOperationException("Module did not return a promise.");
            void PostFinish(bool success, JsValue reason)
            {
                if (!PostTask(source, () =>
                {
                    if (!success) _report?.Invoke(new HtmlScriptError(source, new JsException(reason)));
                    FinishScript(script, done, success);
                })) FailToSchedule();
            }
            then.Call(promise, [HostFunction((_, _) => { PostFinish(true, JsValue.Undefined); return JsValue.Undefined; }, 1),
                HostFunction((_, args) => { PostFinish(false, Arg(args, 0)); return JsValue.Undefined; }, 1)]);
            started = true;
        });
        if (!started) FinishScript(script, done, success: false);
    }

    private sealed record Script(IElement Element, int Index, bool Module, string? Src, string Text, bool Async, bool Deferred)
    {
        public bool External => Src is not null;
    }
    private sealed record ModuleSource(Uri Address, JsParsedModule Parsed, long Size);
    private sealed record ModuleEntry(JsParsedModule Parsed, IReadOnlyDictionary<string, Uri> Resolutions);
    private sealed record ModuleGraph(Uri Root, Dictionary<Uri, ModuleEntry> Sources);
}
