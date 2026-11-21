namespace ShoneHtml;

// The renderer session is the sole reader. UI and history callbacks enqueue synchronously.
internal sealed class HtmlRendererInputQueue
{
    private const int Capacity = 80;
    private readonly object _gate = new();
    private readonly Queue<HtmlRendererInput> _queue = new();
    // One permit per input plus one to wake the reader on completion. No OS wait handle is used.
    private readonly SemaphoreSlim _available = new(0, Capacity + 1);
    private bool _completed;

    internal bool TryEnqueue(HtmlRendererInput input)
    {
        if (input.Text?.Length > 32768) return false;

        // Even consecutive Text updates can run page script. Keep every accepted event in FIFO order.
        lock (_gate)
        {
            if (_completed || _queue.Count >= Capacity) return false;
            _queue.Enqueue(input);
            _available.Release();
            return true;
        }
    }

    // Null requests an idle poll. The timed wait leaves no pending reader on timeout or cancellation.
    internal async ValueTask<HtmlRendererInput?> ReadAsync(TimeSpan idleInterval, CancellationToken token)
    {
        if (!await _available.WaitAsync(idleInterval, token).ConfigureAwait(false) && !_available.Wait(0, token))
            return null;

        lock (_gate)
        {
            if (_completed) throw new InvalidOperationException("Renderer input queue is closed.");
            return _queue.Dequeue();
        }
    }

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed) return;
            _completed = true;
            _queue.Clear();
            _available.Release();
        }
    }
}
