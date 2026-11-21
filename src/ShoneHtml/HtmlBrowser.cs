using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;

namespace ShoneHtml;

/// <summary>Screen dimensions in CSS pixels and the current window's device scale.</summary>
public readonly record struct HtmlDisplayMetrics(int Width, int Height, double DevicePixelRatio);

public sealed class HtmlBrowserOptions
{
    /// <summary>Runs scripts in the browser process for explicitly trusted applications only.</summary>
    public bool EnableTrustedScripts { get; init; }
    public int MaximumResponseBytes { get; init; } = 4 * 1024 * 1024;
    public int MaximumPageResourceBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumPageResources { get; init; } = 48;
    public int MaximumDocumentNodes { get; init; } = 20_000;
    public int MaximumDocumentDepth { get; init; } = 96;
    public int MaximumRedirects { get; init; } = 10;
    public int MaximumHistoryEntries { get; init; } = 50;
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public HtmlPageOptions ScriptOptions { get; init; } = new();
    /// <summary>Optional host display metrics, read on the browser's synchronization context.</summary>
    public Func<HtmlDisplayMetrics>? GetDisplayMetrics { get; init; }
}

/// <summary>A single browsing context. Use its synchronization context when accessing a live page.</summary>
public sealed class HtmlBrowser : IDisposable
{
    private sealed record Navigation(Uri Address, HttpMethod Method, byte[]? Body = null, string? EncodedQuery = null);
    private sealed record Entry(Navigation Request, Uri Address, long DocumentId, double Scroll, object? State = null);
    private readonly List<Entry> _history = [];
    private readonly BrowserContext? _ownedContext;
    private CancellationTokenSource? _navigation;
    private int _index = -1;
    private long _documentId;
    private long _nextDocumentId;
    private bool _disposed;

    public HtmlBrowser(HtmlBrowserOptions? options = null, SynchronizationContext? context = null)
    {
        Options = options ?? new HtmlBrowserOptions();
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
    }

    public HtmlPage? Page { get; private set; }
    public Uri? Address => Page?.Address;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index + 1 < _history.Count;
    public bool IsLoading { get; private set; }
    public string Status { get; private set; } = "";
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

    public void UpdateScroll(double offset) => Run(() =>
    {
        if (_index < 0 || !double.IsFinite(offset))
            return;
        ScrollOffset = Math.Max(0, offset);
        _history[_index] = _history[_index] with { Scroll = ScrollOffset };
    });

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
        var entry = new Entry(new(uri, HttpMethod.Get), uri, _documentId, ScrollOffset, state);
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
            SetViewport(entry.Scroll, null);
            Status = DocumentStatus(page);
            IsLoading = false;
            Changed?.Invoke();
            page.NotifyHistory(before, traversing: true);
            return Task.CompletedTask;
        }
        return NavigateCoreAsync(entry.Request, target, allowFragment: false);
    }

    private async Task NavigateCoreAsync(Navigation request, int existingIndex = -1, bool allowFragment = true,
        bool replace = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
            var entry = new Entry(request, request.Address, _documentId, 0);
            if (replace && _index >= 0)
                _history[_index] = entry;
            else
                Push(entry);
            page.Address = request.Address;
            SetViewport(0, GetFragment(request.Address));
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
        try
        {
            var resource = await Resources.LoadAsync(request.Address, operation.Token, request.Method, request.Body,
                request.Body is null ? null : "application/x-www-form-urlencoded", encodedFormQuery: request.EncodedQuery);
            pending = await HtmlPage.LoadDocumentAsync(this, resource, operation.Token);
            operation.Token.ThrowIfCancellationRequested();

            var previous = Page;
            Page = pending;
            pending = null;
            previous?.Dispose();
            if (existingIndex < 0)
            {
                _documentId = ++_nextDocumentId;
                var entry = new Entry(request, resource.Address, _documentId, 0);
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
            SetViewport(existingIndex < 0 ? 0 : _history[_index].Scroll,
                existingIndex < 0 ? GetFragment(resource.Address) : null);
            Status = resource.IsSuccess ? DocumentStatus(Page) : $"HTTP {(int)resource.Status} {resource.Status}";
            Changed?.Invoke();

            if (resource.IsSuccess && RunsScripts)
                await Page.LoadScriptsAsync(Options.ScriptOptions);
            operation.Token.ThrowIfCancellationRequested();
            IsLoading = false;
            Changed?.Invoke();
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
            pending?.Dispose();
            if (ReferenceEquals(_navigation, operation))
                _navigation = null;
        }
    }

    private bool RunsScripts => Options.EnableTrustedScripts && Options.ScriptOptions.EnableScripts;

    private string DocumentStatus(HtmlPage page)
    {
        if (RunsScripts || !page.Document.QuerySelectorAll("script").Any(HtmlScriptHost.IsExecutableScript))
            return page.ResourceStatus;
        const string disabled = "Page scripts were not run (disabled).";
        return page.ResourceStatus.Length == 0 ? disabled : $"{disabled} {page.ResourceStatus}";
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

    private void SetViewport(double offset, string? fragment)
    {
        ScrollOffset = offset;
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
        });
        _ownedContext?.Dispose();
    }

    private sealed class BrowserContext : SynchronizationContext, IDisposable
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
