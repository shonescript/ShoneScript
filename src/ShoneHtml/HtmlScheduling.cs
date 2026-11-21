using ShoneJs.Runtime;

namespace ShoneHtml;

// Scheduling APIs share the page task queue and timer budget. They do not promise
// renderer paint synchronization: this host has no independent compositor clock.
internal sealed partial class HtmlScriptHost
{
    private const int SchedulingLimit = 128;
    private readonly long _schedulingOrigin = System.Diagnostics.Stopwatch.GetTimestamp();
    private double SchedulingTime => System.Diagnostics.Stopwatch.GetElapsedTime(_schedulingOrigin).TotalMilliseconds;

    private void BindScheduling()
    {
        Global("requestAnimationFrame", 1, (_, args) => ScheduleFrame(args));
        Global("cancelAnimationFrame", 1, (_, args) => { ClearTimer(ToId(Arg(args, 0))); return JsValue.Undefined; });
        Global("requestIdleCallback", 1, (_, args) => ScheduleIdle(args));
        Global("cancelIdleCallback", 1, (_, args) => { ClearTimer(ToId(Arg(args, 0))); return JsValue.Undefined; });

        var scheduler = NewObject();
        Method(scheduler, "postTask", 1, (_, args) => PostScheduledTask(args));
        Method(scheduler, "yield", 0, (_, _) =>
        {
            var promise = _engine.CreatePromise(out var resolve, out var reject);
            if (!PostTask("scheduler.yield", () => resolve(JsValue.Undefined))) reject(Error("AbortError", "The task was aborted"));
            return promise;
        });
        Accessor(_window, "scheduler", _ => JsValue.From(scheduler));
    }

    private JsValue ScheduleFrame(JsValue[] args)
    {
        if (Arg(args, 0).Data is not JsFunction callback) throw TypeError("requestAnimationFrame callback must be a function");
        return ScheduleSpecial("requestAnimationFrame", callback, 16, (fn, now) => _engine.Invoke(fn, JsValue.From(_window), [JsValue.From(now)]));
    }

    private JsValue ScheduleIdle(JsValue[] args)
    {
        if (Arg(args, 0).Data is not JsFunction callback) throw TypeError("requestIdleCallback callback must be a function");
        var timeout = 0;
        if (args.Length > 1 && args[1].Data is JsObject options)
        {
            var value = options.Get("timeout").ToNumber();
            if (double.IsFinite(value) && value > 0) timeout = (int)Math.Min(value, int.MaxValue);
        }
        var scheduledAt = SchedulingTime;
        return ScheduleSpecial("requestIdleCallback", callback, 1, (fn, start) =>
        {
            var deadline = NewObject();
            var didTimeout = timeout > 0 && start - scheduledAt >= timeout;
            Accessor(deadline, "didTimeout", _ => JsValue.From(didTimeout));
            Method(deadline, "timeRemaining", 0, (_, _) => JsValue.From(didTimeout ? 0 : Math.Max(0, 50d - (SchedulingTime - start))));
            _engine.Invoke(fn, JsValue.From(_window), [JsValue.From(deadline)]);
        });
    }

    private JsValue ScheduleSpecial(string source, JsFunction callback, int delay, Action<JsFunction, double> invoke)
    {
        lock (_timers)
        {
            if (_timersDisposed || _page.Lifetime.IsCancellationRequested) return JsValue.From(0d);
            if (_timers.Count >= SchedulingLimit) throw BudgetError("Page scheduling limit exceeded");
            var id = ++_nextTimer;
            var timer = new Timer(_ =>
            {
                if (!PostTask(source, () =>
                {
                    lock (_timers) if (!_timers.ContainsKey(id)) return;
                    ClearTimer(id);
                    invoke(callback, SchedulingTime);
                })) ClearTimer(id);
            });
            _timers.Add(id, timer);
            timer.Change(Math.Max(1, delay), Timeout.Infinite);
            return JsValue.From((double)id);
        }
    }

    private JsValue PostScheduledTask(JsValue[] args)
    {
        if (Arg(args, 0).Data is not JsFunction callback) throw TypeError("scheduler.postTask callback must be a function");
        var promise = _engine.CreatePromise(out var resolve, out var reject);
        var delay = 0;
        if (args.Length > 1 && args[1].Data is JsObject options)
        {
            var value = options.Get("delay").ToNumber();
            if (double.IsFinite(value) && value > 0) delay = (int)Math.Min(value, int.MaxValue);
        }
        if (delay == 0)
        {
            if (!PostTask("scheduler.postTask", () => { try { resolve(_engine.Invoke(callback, JsValue.From(_window), [])); } catch (JsException e) { reject(e.Value); } }))
                reject(Error("AbortError", "The task was aborted."));
        }
        else
        {
            ScheduleSpecial("scheduler.postTask", callback, delay, (fn, _) =>
            {
                try { resolve(_engine.Invoke(fn, JsValue.From(_window), [])); } catch (JsException e) { reject(e.Value); }
            });
        }
        return promise;
    }

    private static int ToId(JsValue value) => value.Type is JsType.Number && double.IsFinite(value.ToNumber()) ? (int)value.ToNumber() : 0;
}
