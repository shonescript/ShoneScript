using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private void BindAbort()
    {
        _signalPrototype = new JsObject { Prototype = _eventTargetPrototype };
        Accessor(_signalPrototype, "aborted", e => JsValue.From(AsSignal(e).Canceled));
        Accessor(_signalPrototype, "reason", e => AsSignal(e).Reason);
        _signalPrototype.DefineOwnProperty("onabort", PropertyDescriptor.Data(JsValue.Null, writable: true, configurable: true));
        Method(_signalPrototype, "throwIfAborted", 0, (e, _) =>
        {
            var signal = AsSignal(e);
            if (signal.Canceled) throw new JsException(signal.Reason);
            return JsValue.Undefined;
        });
        var controller = NewObject();
        Accessor(controller, "signal", e => JsValue.From(AsController(e).Signal));
        Method(controller, "abort", 0, (e, a) =>
        {
            Abort(AsController(e).Signal, AbortReason(Arg(a, 0)));
            return JsValue.Undefined;
        });
        Constructor("AbortController", 0, _ => JsValue.From(new AbortController(NewSignal()) { Prototype = controller }), controller);
        Constructor("AbortSignal", 0, _ => throw TypeError("Illegal constructor"), _signalPrototype);
        var constructor = (JsObject)_engine.GetGlobal("AbortSignal").Data!;
        Method(constructor, "abort", 0, (_, a) =>
        {
            var signal = NewSignal();
            signal.Cancel(AbortReason(Arg(a, 0)));
            return JsValue.From(signal);
        });
        Method(constructor, "timeout", 1, (_, a) =>
        {
            var ms = Math.Truncate(Arg(a, 0).ToNumber());
            if (!double.IsFinite(ms) || ms < 0 || ms > int.MaxValue) throw TypeError("Invalid or out-of-range timeout");
            var signal = NewSignal();
            ScheduleTimer([HostFunction((_, _) =>
            {
                Abort(signal, Error("TimeoutError", "The operation timed out"));
                return JsValue.Undefined;
            }, 0), JsValue.From(ms)], repeat: false);
            return JsValue.From(signal);
        });
        Method(constructor, "any", 1, (_, a) =>
        {
            if (Arg(a, 0).Data is not JsArray values) throw TypeError("Expected an array of AbortSignals");
            const int limit = 1024;
            if (values.Length > limit) throw BudgetError("AbortSignal source limit exceeded");
            var inputs = new AbortSignal[values.Length];
            for (var i = 0; i < inputs.Length; i++)
                inputs[i] = AsSignal(values.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var signal = NewSignal();
            if (inputs.FirstOrDefault(input => input.Canceled) is { } aborted)
            {
                signal.Cancel(aborted.Reason);
                return JsValue.From(signal);
            }
            var sources = new HashSet<AbortSignal>();
            foreach (var input in inputs)
            {
                if (input.Sources is null) sources.Add(input); else sources.UnionWith(input.Sources);
                if (sources.Count > limit) throw BudgetError("AbortSignal source limit exceeded");
            }
            signal.Sources = sources.ToArray();
            foreach (var source in sources)
            {
                var dependents = source.Dependents ??= [];
                dependents.RemoveAll(reference => !reference.TryGetTarget(out var target) || target.Canceled);
                if (dependents.Count >= limit) throw BudgetError("AbortSignal dependency limit exceeded");
                dependents.Add(new WeakReference<AbortSignal>(signal));
            }
            return JsValue.From(signal);
        });
    }

    private static JsValue AbortReason(JsValue reason) => reason.Type == JsType.Undefined ? Error("AbortError", "The operation was aborted") : reason;

    private void Abort(AbortSignal signal, JsValue reason)
    {
        if (signal.Canceled) return;
        signal.Cancel(reason);
        var dependents = new List<AbortSignal>();
        if (signal.Dependents is { } references)
        {
            signal.Dependents = null;
            foreach (var reference in references)
            {
                if (!reference.TryGetTarget(out var dependent) || dependent.Canceled) continue;
                dependent.Cancel(reason);
                dependent.Sources = null;
                dependents.Add(dependent);
            }
        }
        Dispatch(signal, NewEvent("abort", false, false));
        foreach (var dependent in dependents) Dispatch(dependent, NewEvent("abort", false, false));
    }
}
