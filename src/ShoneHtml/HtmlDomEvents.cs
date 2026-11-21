using System.Runtime.CompilerServices;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private readonly ConditionalWeakTable<JsObject, Dictionary<string, List<Listener>>> _listeners = new();
    private readonly JsObject _eventTargetPrototype;
    private JsObject _eventPrototype = null!;

    private void BindEvent()
    {
        BindListeners(_eventTargetPrototype);
        Constructor("EventTarget", 0, _ => JsValue.From(new StandaloneEventTarget { Prototype = _eventTargetPrototype }), _eventTargetPrototype);
        foreach (var type in new[] { "click", "dblclick", "change", "input", "beforeinput", "submit", "reset", "load", "error", "focus", "blur", "focusin", "focusout", "keydown", "keyup", "mousedown", "mouseup", "mousemove", "pointerdown", "pointerup" })
            _elementPrototype.DefineOwnProperty("on" + type, PropertyDescriptor.Data(JsValue.Null, writable: true, configurable: true));
        _eventPrototype = NewObject();
        Accessor(_eventPrototype, "type", e => JsValue.From(Event(e).Type));
        Accessor(_eventPrototype, "target", e => Event(e).Target);
        Accessor(_eventPrototype, "currentTarget", e => Event(e).Current);
        Accessor(_eventPrototype, "bubbles", e => JsValue.From(Event(e).Bubbles));
        Accessor(_eventPrototype, "cancelable", e => JsValue.From(Event(e).Cancelable));
        Accessor(_eventPrototype, "defaultPrevented", e => JsValue.From(Event(e).Prevented));
        Accessor(_eventPrototype, "eventPhase", e => JsValue.From((double)Event(e).Phase));
        Method(_eventPrototype, "preventDefault", 0, (e, _) =>
        {
            var evt = Event(e);
            if (evt.Cancelable && !evt.Passive) evt.Prevented = true;
            return JsValue.Undefined;
        });
        Method(_eventPrototype, "stopPropagation", 0, (e, _) =>
        {
            Event(e).Stopped = true;
            return JsValue.Undefined;
        });
        Method(_eventPrototype, "stopImmediatePropagation", 0, (e, _) =>
        {
            Event(e).Stopped = Event(e).Immediate = true;
            return JsValue.Undefined;
        });
        Constructor("Event", 1, args => JsValue.From(NewEvent(RequiredEventType(args),
            Option(Arg(args, 1), "bubbles"), Option(Arg(args, 1), "cancelable"))), _eventPrototype);
        var customPrototype = new JsObject { Prototype = _eventPrototype };
        Accessor(customPrototype, "detail", e => e.Data is DomEvent { Detail: { } detail } ? detail : throw TypeError("Expected a CustomEvent"));
        Constructor("CustomEvent", 1, args =>
        {
            var type = RequiredEventType(args);
            var options = Arg(args, 1);
            var detail = (options.Data as JsObject)?.Get("detail") ?? JsValue.Undefined;
            return JsValue.From(new DomEvent(type, Option(options, "bubbles"), Option(options, "cancelable"))
            {
                Prototype = customPrototype,
                Detail = detail.Type == JsType.Undefined ? JsValue.Null : detail
            });
        }, customPrototype);
        var togglePrototype = new JsObject { Prototype = _eventPrototype };
        Accessor(togglePrototype, "oldState", e => ((DomEvent)e.Data!).OldState);
        Accessor(togglePrototype, "newState", e => ((DomEvent)e.Data!).NewState);
        Accessor(togglePrototype, "source", e => ((DomEvent)e.Data!).Source);
        Constructor("ToggleEvent", 1, args =>
        {
            var type = RequiredEventType(args);
            var options = Arg(args, 1).Data as JsObject;
            return JsValue.From(new DomEvent(type, Option(Arg(args, 1), "bubbles"), Option(Arg(args, 1), "cancelable"))
            {
                Prototype = togglePrototype,
                OldState = options?.Get("oldState") ?? JsValue.From(""),
                NewState = options?.Get("newState") ?? JsValue.From(""),
                Source = options?.Get("source") ?? JsValue.Null
            });
        }, togglePrototype);
    }

    private void BindListeners(JsObject target)
    {
        Method(target, "addEventListener", 2, (receiver, args) =>
        {
            var owner = ListenerTarget(receiver);
            if (Arg(args, 1).Data is not JsObject callback) return JsValue.Undefined;
            var type = Arg(args, 0).ToJsString();
            var options = Arg(args, 2);
            var capture = options.Data is JsObject ? Option(options, "capture") : options.ToBoolean();
            var byType = _listeners.GetOrCreateValue(owner);
            if (!byType.TryGetValue(type, out var list)) byType.Add(type, list = []);
            if (!list.Any(entry => ReferenceEquals(entry.Callback, callback) && entry.Capture == capture))
                list.Add(new Listener(callback, capture, Option(options, "once"), Option(options, "passive")));
            return JsValue.Undefined;
        });
        Method(target, "removeEventListener", 2, (receiver, args) =>
        {
            var owner = ListenerTarget(receiver);
            var options = Arg(args, 2);
            var capture = options.Data is JsObject ? Option(options, "capture") : options.ToBoolean();
            if (_listeners.TryGetValue(owner, out var types) && types.TryGetValue(Arg(args, 0).ToJsString(), out var list) &&
                list.Find(entry => ReferenceEquals(entry.Callback, Arg(args, 1).Data) && entry.Capture == capture) is { } entry)
                Remove(list, entry);
            return JsValue.Undefined;
        });
        Method(target, "dispatchEvent", 1, (receiver, args) => JsValue.From(Dispatch(ListenerTarget(receiver), Event(Arg(args, 0)))));
    }

    private JsObject ListenerTarget(JsValue value) => value.Data is NodeObject or StandaloneEventTarget or AbortSignal
        ? (JsObject)value.Data : ReferenceEquals(value.Data, _window) ? _window : throw TypeError("Expected an event target");

    private static string RequiredEventType(JsValue[] args) => args.Length != 0 ? args[0].ToJsString() : throw TypeError("Expected an event type");
    private sealed class StandaloneEventTarget : JsObject;

    private DomEvent NewEvent(string type, bool bubbles, bool cancelable) => new(type, bubbles, cancelable) { Prototype = _eventPrototype };
    private static DomEvent Event(JsValue value) => value.Data as DomEvent ?? throw TypeError("Expected an Event");
    private static bool Option(JsValue options, string name) => options.Data is JsObject obj && obj.Get(name).ToBoolean();

    private void DispatchImageEvent(AngleSharp.Dom.IElement image, bool loaded) =>
        Dispatch(GetWrapper(image), NewEvent(loaded ? "load" : "error", bubbles: false, cancelable: false));

    private bool DispatchHostEvent(JsObject target, DomEvent evt)
    {
        if (_page.Lifetime.IsCancellationRequested) return true;
        var result = Dispatch(target, evt);
        if (_dispatchDepth == 0)
        {
            DrainJobs(evt.Type);
            _page.PublishChanges();
        }
        return result;
    }

    private bool Dispatch(JsObject target, DomEvent evt)
    {
        if (evt.Dispatching || evt.Type.Length == 0) throw TypeError("Event is not dispatchable");
        if (_dispatchDepth >= 64) throw TypeError("Event dispatch depth exceeded");
        var path = new List<JsObject> { target };
        if (target is NodeObject wrapper)
        {
            for (var node = wrapper.Node.Parent; node is not null; node = node.Parent)
                path.Add(GetWrapper(node));
            if (ReferenceEquals(((NodeObject)path[^1]).Node, _page.Document)) path.Add(_window);
        }
        evt.Target = JsValue.From(target);
        evt.Dispatching = true;
        _dispatchDepth++;
        try
        {
            for (var i = path.Count - 1; i > 0 && !evt.Stopped; i--)
                InvokeListeners(path[i], evt, 1, true);
            if (!evt.Stopped)
            {
                var targetListeners = Snapshot(target, evt.Type);
                InvokeListeners(target, evt, 2, true, targetListeners);
                if (!evt.Immediate) InvokeListeners(target, evt, 2, false, targetListeners);
            }
            if (evt.Bubbles)
                for (var i = 1; i < path.Count && !evt.Stopped; i++)
                    InvokeListeners(path[i], evt, 3, false);
            return !evt.Prevented;
        }
        finally
        {
            _dispatchDepth--;
            evt.Current = JsValue.Null;
            evt.Phase = 0;
            evt.Passive = evt.Stopped = evt.Immediate = evt.Dispatching = false;
        }
    }

    private (List<Listener> List, Listener[] Copy)? Snapshot(JsObject target, string type) =>
        _listeners.TryGetValue(target, out var types) && types.TryGetValue(type, out var list) ? (list, list.ToArray()) : null;

    private void InvokeListeners(JsObject target, DomEvent evt, int phase, bool capture, (List<Listener> List, Listener[] Copy)? snapshot = null)
    {
        evt.Current = JsValue.From(target);
        evt.Phase = phase;
        var arguments = new[] { JsValue.From(evt) };
        if ((snapshot ?? Snapshot(target, evt.Type)) is { } listeners)
        {
            foreach (var entry in listeners.Copy)
            {
                if (evt.Immediate) break;
                if (entry.Removed || entry.Capture != capture) continue;
                if (entry.Once) Remove(listeners.List, entry);
                evt.Passive = entry.Passive;
                Run(evt.Type, () =>
                {
                    if (entry.Callback is JsFunction function) _engine.Invoke(function, evt.Current, arguments);
                    else if (entry.Callback.Get("handleEvent").Data is JsFunction handler)
                        _engine.Invoke(handler, JsValue.From(entry.Callback), arguments);
                });
                evt.Passive = false;
            }
        }
        if (!capture && !evt.Immediate)
            Run(evt.Type, () =>
            {
                if (target.Get("on" + evt.Type).Data is JsFunction handler &&
                    _engine.Invoke(handler, evt.Current, arguments).Equals(JsValue.From(false)) && evt.Cancelable)
                    evt.Prevented = true;
            });
    }

    private static void Remove(List<Listener> list, Listener entry)
    {
        entry.Removed = true;
        list.Remove(entry);
    }

    private sealed class Listener(JsObject callback, bool capture, bool once, bool passive)
    {
        public JsObject Callback { get; } = callback;
        public bool Capture { get; } = capture;
        public bool Once { get; } = once;
        public bool Passive { get; } = passive;
        public bool Removed { get; set; }
    }

    private sealed class DomEvent(string type, bool bubbles, bool cancelable) : JsObject
    {
        public string Type { get; } = type;
        public bool Bubbles { get; } = bubbles;
        public bool Cancelable { get; } = cancelable;
        public JsValue? Detail { get; init; }
        public JsValue OldState { get; init; } = JsValue.From("");
        public JsValue NewState { get; init; } = JsValue.From("");
        public JsValue Source { get; init; } = JsValue.Null;
        public JsValue Target = JsValue.Null;
        public JsValue Current = JsValue.Null;
        public int Phase;
        public bool Dispatching, Stopped, Immediate, Prevented, Passive;
    }
}
