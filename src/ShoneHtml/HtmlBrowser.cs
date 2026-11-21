using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace ShoneHtml;

/// <summary>Screen dimensions in CSS pixels and the current window's device scale.</summary>
public readonly record struct HtmlDisplayMetrics(int Width, int Height, double DevicePixelRatio);

/// <summary>Controls JavaScript execution and the network transport used by browser navigation.</summary>
public enum HtmlBrowserScriptMode
{
    /// <summary>Uses <see cref="Isolated"/> unless <see cref="HtmlBrowserOptions.EnableTrustedScripts"/> opts into <see cref="TrustedInProcess"/>.</summary>
    Auto,
    /// <summary>Uses the restricted public network transport and leaves page scripts disabled.</summary>
    Disabled,
    /// <summary>Uses the trusted transport and runs scripts in process when enabled. For application-trusted content only.</summary>
    TrustedInProcess,
    /// <summary>Uses the restricted public network transport. Runs enabled scripts only in a released, available isolated renderer and renders without scripts otherwise.</summary>
    Isolated
}

public sealed class HtmlBrowserOptions
{
    /// <summary>Legacy opt-in to <see cref="HtmlBrowserScriptMode.TrustedInProcess"/> when <see cref="ScriptMode"/> is <see cref="HtmlBrowserScriptMode.Auto"/>.</summary>
    public bool EnableTrustedScripts { get; init; }
    /// <summary>Selects the browser script policy. The default is <see cref="HtmlBrowserScriptMode.Auto"/>.</summary>
    public HtmlBrowserScriptMode ScriptMode { get; init; }
    // Modern sites such as Bing commonly split the page across many image, CSS and script resources.
    public int MaximumResponseBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumPageResourceBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumPageResources { get; init; } = 128;
    public int MaximumDocumentNodes { get; init; } = 20_000;
    public int MaximumDocumentDepth { get; init; } = 96;
    public int MaximumRedirects { get; init; } = 10;
    public int MaximumHistoryEntries { get; init; } = 50;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>Configures script execution within the selected <see cref="ScriptMode"/>. Disabling scripts here also suppresses renderer startup.</summary>
    public HtmlPageOptions ScriptOptions { get; init; } = new();
    /// <summary>Optional host display metrics, read on the browser's synchronization context.</summary>
    public Func<HtmlDisplayMetrics>? GetDisplayMetrics { get; init; }
}

/// <summary>A single browsing context. Use its synchronization context when accessing a live page.</summary>
public sealed class HtmlBrowser : IDisposable
{
    private sealed record Navigation(Uri Address, HttpMethod Method, byte[]? Body = null, string? EncodedQuery = null);
    private sealed record Entry(Navigation Request, Uri Address, long DocumentId, double ScrollX, double Scroll, object? State = null);
    private readonly List<Entry> _history = [];
    private readonly BrowserContext? _ownedContext;
    private readonly HtmlPublicNetwork? _publicNetwork;
    private long _rendererGeneration;
    private int _rendererNavigations;
    private CancellationTokenSource? _navigation;
    private int _index = -1;
    private long _documentId;
    private long _nextDocumentId;
    private bool _disposed;

    public HtmlBrowser(HtmlBrowserOptions? options = null, SynchronizationContext? context = null)
    {
        Options = options ?? new HtmlBrowserOptions();
        if (!Enum.IsDefined(Options.ScriptMode)) throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaximumResponseBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaximumPageResourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumPageResources);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaximumDocumentNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaximumDocumentDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.MaximumRedirects);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Options.MaximumHistoryEntries);
        if (Options.RequestTimeout <= TimeSpan.Zero || Options.RequestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Request timeout must be positive and finite.");
        ArgumentNullException.ThrowIfNull(Options.ScriptOptions);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.ScriptOptions.MaximumLoopIterations);
        Context = context ?? (_ownedContext = new BrowserContext());
        Resources = new HtmlResources(Options);
        if (!UsesTrustedTransport) _publicNetwork = new HtmlPublicNetwork(Options);
    }

    public HtmlPage? Page { get; private set; }
    public Uri? Address => Page?.Address;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index + 1 < _history.Count;
    public bool IsLoading { get; private set; }
    public string Status { get; private set; } = "";
    public double ScrollOffsetX { get; private set; }
    public double ScrollOffset { get; private set; }
    public string? ScrollFragment { get; private set; }
    public long NavigationVersion { get; private set; }
    public event Action? Changed;

    internal static string Platform { get; } = $"{(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Unknown")} {RuntimeInformation.OSArchitecture}";
    private static readonly Version ProductVersion = typeof(HtmlBrowser).Assembly.GetName().Version ?? new Version(1, 0);
    internal static string UserAgent { get; } = $"ShoneHtml/{ProductVersion} ({Platform})";

    internal HtmlBrowserOptions Options { get; }
    internal HtmlResources Resources { get; }
    internal SynchronizationContext Context { get; }
    internal int HistoryLength => _history.Count;
    internal object? HistoryState => _index < 0 ? null : _history[_index].State;

    public Task NavigateAsync(string address) => RunAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(address);
        address = address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal))
            address = "https://" + address;
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? NavigateCoreAsync(new(uri, HttpMethod.Get)) :
            RejectAddress();
    });

    public Task NavigateAsync(Uri address) => RunAsync(() => NavigateCoreAsync(new(address, HttpMethod.Get)));
    internal Task NavigateReplacingAsync(Uri address) => RunAsync(() => NavigateCoreAsync(new(address, HttpMethod.Get), replace: true));
    internal Task SubmitAsync(HtmlFormRequest submission) => RunAsync(() =>
        NavigateCoreAsync(new(submission.Address, submission.Method, submission.Body, submission.EncodedQuery), allowFragment: false));
    public Task GoBackAsync() => GoAsync(-1);
    public Task GoForwardAsync() => GoAsync(1);

    public Task GoAsync(int delta) => delta == 0 ? RefreshAsync() : RunAsync(() =>
    {
        var target = (long)_index + delta;
        return target >= 0 && target < _history.Count ? TraverseAsync((int)target) : Task.CompletedTask;
    });

    public Task RefreshAsync() => RunAsync(() => _index < 0 ? Task.CompletedTask :
        NavigateCoreAsync(_history[_index].Request, _index, allowFragment: false));

    public void UpdateScroll(double offset) => Run(() => StoreScroll(ScrollOffsetX, offset));

    public void UpdateScroll(double x, double y) => Run(() => StoreScroll(x, y));

    private void StoreScroll(double x, double y)
    {
        if (_index < 0 || !double.IsFinite(x) || !double.IsFinite(y))
            return;
        ScrollOffsetX = Math.Max(0, x);
        ScrollOffset = Math.Max(0, y);
        _history[_index] = _history[_index] with { ScrollX = ScrollOffsetX, Scroll = ScrollOffset };
    }

    public void Stop() => Run(() =>
    {
        if (_disposed)
            return;
        _navigation?.Cancel();
        Page?.Stop();
        IsLoading = false;
        Status = "Stopped";
        Changed?.Invoke();
    });

    internal void SetHistoryAddress(HtmlPage page, Uri uri, bool replace, object? state = null)
    {
        if (Page != page || page.Lifetime.IsCancellationRequested || _index < 0 || !HtmlResources.IsHttpAddress(uri) ||
            !HtmlResources.SameOrigin(page.Address!, uri))
            throw new InvalidOperationException("History URLs must be in the active document's origin.");
        var entry = new Entry(new(uri, HttpMethod.Get), uri, _documentId, ScrollOffsetX, ScrollOffset, state);
        if (replace)
            _history[_index] = entry;
        else
            Push(entry);
        page.Address = uri;
        Changed?.Invoke();
    }

    private Task TraverseAsync(int target)
    {
        var entry = _history[target];
        if (Page is { } page && _documentId == entry.DocumentId)
        {
            var before = page.Address!;
            _navigation?.Cancel();
            _index = target;
            page.Address = entry.Address;
            SetViewport(entry.ScrollX, entry.Scroll, null);
            Status = DocumentStatus(page);
            IsLoading = false;
            Changed?.Invoke();
            page.NotifyHistory(before, traversing: true);
            return Task.CompletedTask;
        }
        return NavigateCoreAsync(entry.Request, target, allowFragment: false);
    }

    private async Task NavigateCoreAsync(Navigation request, int existingIndex = -1, bool allowFragment = true,
        bool replace = false, bool rendererInitiated = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!rendererInitiated) _rendererNavigations = 0;
        if (!HtmlResources.IsHttpAddress(request.Address))
        {
            await RejectAddress();
            return;
        }

        _navigation?.Cancel();
        if (allowFragment && Page is { Address: { } current } page &&
            (request.Address.Fragment.Length > 0 || current.Fragment.Length > 0) &&
            current.AbsoluteUri != request.Address.AbsoluteUri &&
            HtmlResources.WithoutFragment(current).AbsoluteUri == HtmlResources.WithoutFragment(request.Address).AbsoluteUri)
        {
            var entry = new Entry(request, request.Address, _documentId, 0, 0);
            if (replace && _index >= 0)
                _history[_index] = entry;
            else
                Push(entry);
            page.Address = request.Address;
            SetViewport(0, 0, GetFragment(request.Address));
            Status = DocumentStatus(page);
            IsLoading = false;
            Changed?.Invoke();
            page.NotifyHistory(current, traversing: false);
            return;
        }

        Page?.Stop();
        using var operation = new CancellationTokenSource();
        _navigation = operation;
        IsLoading = true;
        Status = $"Loading {request.Address.Host}…";
        Changed?.Invoke();
        HtmlPage? pending = null;
        HtmlRendererSession? renderer = null;
        HtmlRendererResult? initial = null;
        try
        {
            var scope = _publicNetwork?.CreateScope();
            var contentType = request.Body is null ? null : "application/x-www-form-urlencoded";
            var resource = scope is null
                ? await Resources.LoadAsync(request.Address, operation.Token, request.Method, request.Body, contentType, encodedFormQuery: request.EncodedQuery)
                : await scope.LoadAsync(request.Address, operation.Token, HtmlPublicRequestKind.Navigation, request.Method, request.Body, contentType,
                    encodedFormQuery: request.EncodedQuery);
            if (scope is not null && resource.IsSuccess && WantsRenderer && HtmlRendererSession.IsReleased)
            {
                pending = HtmlPage.CreateRendererMirror(this, resource.Address);
                try
                {
                    (renderer, initial) = await HtmlRendererSession.OpenAsync(scope, resource, ++_rendererGeneration,
                        Options, operation.Token, pending.Lifetime);
                    pending.ApplyRendererSnapshot(initial.Snapshot!, initial.FocusedId);
                    pending.AttachRenderer(renderer);
                }
                catch (Exception ex) when (HtmlRendererSession.IsRendererFailure(ex))
                {
                    renderer?.Dispose();
                    renderer = null;
                    initial = null;
                    pending.Dispose();
                    pending = null;
                    operation.Token.ThrowIfCancellationRequested();
                }
            }
            pending ??= await HtmlPage.LoadDocumentAsync(this, resource, operation.Token, scope);
            operation.Token.ThrowIfCancellationRequested();

            var previous = Page;
            Page = pending;
            pending = null;
            previous?.Dispose();
            if (existingIndex < 0)
            {
                _documentId = ++_nextDocumentId;
                var entry = new Entry(request, resource.Address, _documentId, 0, 0);
                if (replace && _index >= 0)
                    _history[_index] = entry;
                else
                    Push(entry);
            }
            else
            {
                _index = existingIndex;
                var entry = _history[_index];
                _documentId = entry.DocumentId;
                _history[_index] = entry with { Address = resource.Address };
            }
            SetViewport(existingIndex < 0 ? 0 : _history[_index].ScrollX,
                existingIndex < 0 ? 0 : _history[_index].Scroll,
                existingIndex < 0 ? GetFragment(resource.Address) : null);
            Status = resource.IsSuccess ? DocumentStatus(Page) : $"HTTP {(int)resource.Status} {resource.Status}";
            Changed?.Invoke();

            var active = Page;
            if (resource.IsSuccess && RunsScripts)
                await active.LoadScriptsAsync(Options.ScriptOptions);
            operation.Token.ThrowIfCancellationRequested();
            IsLoading = false;
            Changed?.Invoke();
            if (renderer is not null && initial is not null)
            {
                var session = renderer;
                renderer = null; // The page now owns the session.
                var followup = ApplyRendererResult(active, initial with { Snapshot = null });
                _ = session.RunAsync(result => RunAsync(() => ApplyRendererResult(active, result)),
                    () => RunAsync(() => RendererStopped(active)));
                await followup;
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            // A new navigation or Stop owns the visible state.
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            if (!operation.IsCancellationRequested)
            {
                Status = exception.Message;
                IsLoading = false;
                Changed?.Invoke();
            }
        }
        finally
        {
            renderer?.Dispose();
            pending?.Dispose();
            if (ReferenceEquals(_navigation, operation))
                _navigation = null;
        }
    }

    private bool UsesTrustedTransport => Options.ScriptMode == HtmlBrowserScriptMode.TrustedInProcess ||
        Options.ScriptMode == HtmlBrowserScriptMode.Auto && Options.EnableTrustedScripts;
    private bool RunsScripts => UsesTrustedTransport && Options.ScriptOptions.EnableScripts;
    private bool WantsRenderer => Options.ScriptOptions.EnableScripts &&
        (Options.ScriptMode == HtmlBrowserScriptMode.Isolated || Options.ScriptMode == HtmlBrowserScriptMode.Auto && !Options.EnableTrustedScripts);

    private Task ApplyRendererResult(HtmlPage page, HtmlRendererResult result)
    {
        if (!ReferenceEquals(Page, page) || page.Lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (result.Snapshot is { } snapshot) page.ApplyRendererSnapshot(snapshot, result.FocusedId);
        if (result.Error is not null)
        {
            Status = "A page script reported an error.";
            Changed?.Invoke();
        }
        if (!ReferenceEquals(Page, page) || page.Lifetime.IsCancellationRequested || result.Navigation is not { } navigation)
            return Task.CompletedTask;
        // Do not keep the current session pump waiting for another document to load.
        Context.Post(_ =>
        {
            if (!ReferenceEquals(Page, page) || page.Lifetime.IsCancellationRequested) return;
            if (++_rendererNavigations > 16)
            {
                _ = RendererStopped(page);
                return;
            }
            _ = NavigateCoreAsync(new(new Uri(navigation.Address), new HttpMethod(navigation.Method), navigation.Body, navigation.Query),
                allowFragment: navigation.Method == "GET" && navigation.Query is null, replace: navigation.Replace, rendererInitiated: true);
        }, null);
        return Task.CompletedTask;
    }

    private Task RendererStopped(HtmlPage page)
    {
        if (ReferenceEquals(Page, page) && !page.Lifetime.IsCancellationRequested)
        {
            page.Stop();
            Status = "Isolated renderer stopped. The last display is no longer interactive.";
            Changed?.Invoke();
        }
        return Task.CompletedTask;
    }

    private string DocumentStatus(HtmlPage page)
    {
        if (RunsScripts || !page.Document.QuerySelectorAll("script").Any(HtmlScriptHost.IsExecutableScript))
            return page.ResourceStatus;
        var status = WantsRenderer ? "Page scripts were not run (isolated renderer unavailable)." :
            "Page scripts were not run (disabled).";
        return page.ResourceStatus.Length == 0 ? status : $"{status} {page.ResourceStatus}";
    }

    private Task RejectAddress()
    {
        Status = "Enter an HTTP(S) address without user information.";
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    internal void ReportScriptError(HtmlPage page, HtmlScriptError error)
    {
        if (Page != page || page.Lifetime.IsCancellationRequested)
            return;
        Status = $"{error.Source}: {error.Exception.Message}";
        Changed?.Invoke();
    }

    private void Push(Entry entry)
    {
        _history.RemoveRange(_index + 1, _history.Count - _index - 1);
        _history.Add(entry);
        if (_history.Count > Options.MaximumHistoryEntries)
            _history.RemoveAt(0);
        _index = _history.Count - 1;
    }

    private void SetViewport(double x, double y, string? fragment)
    {
        ScrollOffsetX = x;
        ScrollOffset = y;
        ScrollFragment = fragment;
        NavigationVersion++;
    }

    private static string? GetFragment(Uri address) => address.Fragment.Length > 0
        ? Uri.UnescapeDataString(address.Fragment[1..]) : null;

    internal Task RunAsync(Func<Task> action)
    {
        if (SynchronizationContext.Current == Context)
            return action();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Context.Post(async _ =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(Context);
            try { await action(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }, null);
        return completion.Task;
    }

    private void Run(Action action)
    {
        if (SynchronizationContext.Current == Context)
            action();
        else
            Context.Send(_ => action(), null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Run(() =>
        {
            _disposed = true;
            _navigation?.Cancel();
            Page?.Dispose();
            Resources.Dispose();
            _publicNetwork?.Dispose();
        });
        _ownedContext?.Dispose();
    }

    internal sealed class BrowserContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _callbacks = new();
        private readonly Thread _thread;

        public BrowserContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var callback in _callbacks.GetConsumingEnumerable())
                    callback();
            }) { IsBackground = true, Name = "ShoneHtml browsing context" };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            try { _callbacks.Add(() => callback(state)); }
            catch (InvalidOperationException)
            {
                // Run canceled asynchronous continuations so their callers can observe completion.
                ThreadPool.QueueUserWorkItem(_ => callback(state));
            }
        }

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Thread.CurrentThread == _thread)
            {
                callback(state);
                return;
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { callback(state); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
            }, null);
            completion.Task.GetAwaiter().GetResult();
        }

        public void Dispose() => _callbacks.CompleteAdding();
    }
}
