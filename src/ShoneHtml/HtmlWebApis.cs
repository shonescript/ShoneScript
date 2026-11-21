using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private readonly Dictionary<int, Timer> _timers = [];
    private int _nextTimer;
    private bool _timersDisposed;

    private void BindWebApis()
    {
        BindEnvironment();
        BindEvent();
        BindNavigation();
        BindUrl();
        BindFetch();
        Global("setTimeout", 2, (_, args) => ScheduleTimer(args, repeat: false));
        Global("setInterval", 2, (_, args) => ScheduleTimer(args, repeat: true));
        Global("clearTimeout", 1, (_, args) => { ClearTimer((int)Arg(args, 0).ToNumber()); return JsValue.Undefined; });
        Global("clearInterval", 1, (_, args) => { ClearTimer((int)Arg(args, 0).ToNumber()); return JsValue.Undefined; });
        Global("queueMicrotask", 1, (_, args) =>
        {
            if (Arg(args, 0).Data is not JsFunction callback) throw TypeError("Expected a function");
            _engine.EnqueueMicrotask(() => Run("queueMicrotask", () => _engine.Invoke(callback, JsValue.Undefined, [])));
            return JsValue.Undefined;
        });
    }

    private void BindEnvironment()
    {
        var navigator = NewObject();
        Accessor(navigator, "userAgent", _ => JsValue.From(HtmlBrowser.UserAgent));
        Accessor(navigator, "platform", _ => JsValue.From(HtmlBrowser.Platform));
        Accessor(_window, "navigator", _ => JsValue.From(navigator));

        // A host without a display reports zero dimensions and uses unit device scale.
        HtmlDisplayMetrics Metrics() => _page.Browser?.Options.GetDisplayMetrics?.Invoke() ?? new(0, 0, 1);
        var screen = NewObject();
        Accessor(screen, "width", _ => JsValue.From((double)Math.Max(0, Metrics().Width)));
        Accessor(screen, "height", _ => JsValue.From((double)Math.Max(0, Metrics().Height)));
        Accessor(_window, "screen", _ => JsValue.From(screen));
        Accessor(_window, "devicePixelRatio", _ =>
        {
            var scale = Metrics().DevicePixelRatio;
            return JsValue.From(double.IsFinite(scale) && scale > 0 ? scale : 1);
        });

        var css = NewObject();
        Method(css, "supports", 1, (_, args) => JsValue.From(args.Length switch
        {
            >= 2 => HtmlStyles.SupportsDeclaration(args[0].ToJsString(), args[1].ToJsString()),
            1 => HtmlStyles.SupportsCondition(args[0].ToJsString()),
            _ => throw TypeError("CSS.supports requires at least one argument")
        }));
        _engine.SetGlobal("CSS", JsValue.From(css));
    }

    private void Global(string name, int arity, Func<JsValue, JsValue[], JsValue> call) => _engine.SetGlobal(name, HostFunction(call, arity));

    private void Constructor(string name, int arity, Func<JsValue[], JsValue> create, JsObject? prototype = null, bool callable = false)
    {
        var function = new JsFunction((_, args) => callable ? create(args) : throw TypeError($"Use new {name}()"), arity, create);
        if (prototype is not null)
        {
            function.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.From(prototype)));
            prototype.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.From(function), writable: true, configurable: true));
        }
        _engine.SetGlobal(name, JsValue.From(function));
    }

    private JsValue ScheduleTimer(JsValue[] args, bool repeat)
    {
        if (Arg(args, 0).Data is not JsFunction callback) throw TypeError("Timer callbacks must be functions");
        var number = Arg(args, 1).ToNumber();
        var milliseconds = double.IsFinite(number) ? (int)Math.Clamp(number, 1, int.MaxValue) : 1;
        var arguments = args.Length > 2 ? args[2..] : [];
        lock (_timers)
        {
            if (_timersDisposed || _page.Lifetime.IsCancellationRequested) return JsValue.From(0d);
            if (_timers.Count >= 128) throw BudgetError("Page timer limit exceeded");
            var id = ++_nextTimer;
            var timer = new Timer(_ =>
            {
                if (!PostTask(repeat ? "setInterval" : "setTimeout", () =>
                {
                    lock (_timers) if (!_timers.ContainsKey(id)) return;
                    if (!repeat) ClearTimer(id);
                    try { _engine.Invoke(callback, JsValue.From(_window), arguments); }
                    finally
                    {
                        if (repeat) lock (_timers)
                            if (_timers.TryGetValue(id, out var active)) active.Change(milliseconds, Timeout.Infinite);
                    }
                })) ClearTimer(id);
            });
            _timers.Add(id, timer);
            timer.Change(milliseconds, Timeout.Infinite);
            return JsValue.From((double)id);
        }
    }

    private void ClearTimer(int id)
    {
        lock (_timers) if (_timers.Remove(id, out var active)) active.Dispose();
    }

    private void DisposeTimers()
    {
        lock (_timers)
        {
            _timersDisposed = true;
            foreach (var timer in _timers.Values) timer.Dispose();
            _timers.Clear();
        }
    }

    private void BindNavigation()
    {
        var location = NewObject();
        var browser = _page.Browser;
        var renderer = Renderer;
        Uri Current() => _page.Address ?? new Uri("about:blank");
        void Navigate(string text, bool replace = false)
        {
            if (renderer is null && browser is null) return;
            if ((renderer is not null && text.Length > 8192) || !Uri.TryCreate(Current(), text, out var url) || !HtmlResources.IsHttpAddress(url))
                throw TypeError("Invalid navigation URL");
            if (renderer is not null)
            {
                if (url.AbsoluteUri.Length > 8192) throw TypeError("Invalid navigation URL");
                renderer.Navigate(new(url.AbsoluteUri, "GET", Replace: replace));
            }
            else _ = replace ? browser!.NavigateReplacingAsync(url) : browser!.NavigateAsync(url);
        }
        foreach (var name in UrlParts)
        {
            Accessor(location, name, _ => JsValue.From(UrlPart(Current(), name)), name is "origin" or "username" or "password" ? null : (_, value) =>
            {
                if (name == "href") Navigate(value.ToJsString());
                else if (ChangeUrl(Current(), name, value.ToJsString()) is { } updated && updated.AbsoluteUri != Current().AbsoluteUri)
                    Navigate(updated.AbsoluteUri);
            });
        }
        Method(location, "assign", 1, (_, a) => { Navigate(Arg(a, 0).ToJsString()); return JsValue.Undefined; });
        Method(location, "replace", 1, (_, a) => { Navigate(Arg(a, 0).ToJsString(), true); return JsValue.Undefined; });
        if (renderer is null)
            Method(location, "reload", 0, (_, _) => { if (browser is not null) _ = browser.RefreshAsync(); return JsValue.Undefined; });
        Method(location, "toString", 0, (_, _) => JsValue.From(Current().AbsoluteUri));
        Accessor(_window, "location", _ => JsValue.From(location), (_, value) => Navigate(value.ToJsString()));
        Accessor(_documentPrototype, "location", _ => JsValue.From(location), (_, value) => Navigate(value.ToJsString()));

        // Session history belongs to the browser, not to the renderer document.
        if (renderer is not null) return;
        var history = NewObject();
        Accessor(history, "length", _ => JsValue.From((double)(browser?.HistoryLength ?? 1)));
        Accessor(history, "state", _ => ReadHistoryState());
        Method(history, "back", 0, (_, _) => { if (browser is not null) _ = browser.GoAsync(-1); return JsValue.Undefined; });
        Method(history, "forward", 0, (_, _) => { if (browser is not null) _ = browser.GoAsync(1); return JsValue.Undefined; });
        Method(history, "go", 0, (receiver, a) => { if (browser is not null) _ = browser.GoAsync(Arg(a, 0).Type == JsType.Undefined ? 0 : (int)Arg(a, 0).ToNumber()); return JsValue.Undefined; });
        foreach (var replace in new[] { false, true })
        {
            Method(history, replace ? "replaceState" : "pushState", 2, (_, a) =>
            {
                if (browser is null) return JsValue.Undefined;
                var address = Current();
                if (a.Length > 2 && a[2].Type is not (JsType.Null or JsType.Undefined) && a[2].ToJsString() is { Length: > 0 } text)
                {
                    if (!Uri.TryCreate(_page.BaseAddress, text, out var next) || !HtmlResources.IsHttpAddress(next) || !HtmlResources.SameOrigin(address, next))
                        throw new JsException(Error("SecurityError", "History URLs must be same-origin"));
                    address = next;
                }
                var state = Json("stringify", Arg(a, 0));
                if (state.Type == JsType.String && state.ToJsString().Length > 262144) throw TypeError("History state exceeds page limit");
                browser.SetHistoryAddress(_page, address, replace, state.Type == JsType.String ? state.ToJsString() : null);
                return JsValue.Undefined;
            });
        }
        _engine.SetGlobal("history", JsValue.From(history));
    }

    private JsValue ReadHistoryState() => _page.Browser?.HistoryState is string json ? Json("parse", JsValue.From(json)) : JsValue.Null;

    private JsValue Json(string operation, JsValue input) => _jsonMethods[operation].Call(input);
    private readonly Dictionary<string, JsFunction> _jsonMethods = new(StringComparer.Ordinal);

    private static JsValue Error(string name, string message) => JsValue.From(new JsError(name, message));
}
