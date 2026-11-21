namespace ShoneHtml;

// Producers may post from I/O or timer threads; only the page owner runs callbacks.
internal sealed class HtmlTaskQueue(SynchronizationContext? context, CancellationToken lifetime, Action<string, Action> run) : IDisposable
{
    private const int Capacity = 1024;
    private const int TurnSize = 64;
    private readonly Queue<(string Source, Action Work)> _ready = new();
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private bool _scheduled;
    private bool _disposed;
    private bool _overflow;
    private bool _pumping;

    public bool Post(string source, Action work)
    {
        bool notify;
        bool accepted;
        lock (_ready)
        {
            if (_disposed || lifetime.IsCancellationRequested) return false;
            accepted = _ready.Count < Capacity;
            if (accepted) _ready.Enqueue((source, work));
            else _overflow = true;
            notify = context is not null && !_scheduled;
            if (notify) _scheduled = true;
        }
        if (notify) context!.Post(static queue => ((HtmlTaskQueue)queue!).Pump(), this);
        return accepted;
    }

    public void Pump()
    {
        lock (_ready) if (_disposed || lifetime.IsCancellationRequested) return;
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException("Page tasks must run on the page owner thread.");
        if (_pumping) return;
        _pumping = true;
        try
        {
            for (var i = 0; i < TurnSize; i++)
            {
                (string Source, Action Work) item;
                lock (_ready)
                {
                    if (_disposed || lifetime.IsCancellationRequested) break;
                    if (_overflow)
                    {
                        _overflow = false;
                        item = ("task queue", static () => throw new InvalidOperationException("Page task queue limit exceeded."));
                    }
                    else if (!_ready.TryDequeue(out item)) break;
                }
                run(item.Source, item.Work);
            }
        }
        finally
        {
            _pumping = false;
            lock (_ready)
                _scheduled = context is not null && !_disposed && !lifetime.IsCancellationRequested && (_ready.Count > 0 || _overflow);
            if (_scheduled) context!.Post(static queue => ((HtmlTaskQueue)queue!).Pump(), this);
        }
    }

    public void Dispose()
    {
        lock (_ready)
        {
            _disposed = true;
            _ready.Clear();
        }
    }
}
