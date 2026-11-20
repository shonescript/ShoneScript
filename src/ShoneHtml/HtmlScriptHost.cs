using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using ShoneJs;
using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed class HtmlScriptHost
{
    private static readonly HashSet<string> ClassicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text/javascript", "application/javascript", "text/ecmascript", "application/ecmascript",
        "application/x-ecmascript", "application/x-javascript", "text/jscript", "text/livescript",
        "text/x-ecmascript", "text/x-javascript", "text/javascript1.0", "text/javascript1.1",
        "text/javascript1.2", "text/javascript1.3", "text/javascript1.4", "text/javascript1.5"
    };

    private readonly HtmlPage _page;
    private readonly Engine _engine;
    private readonly Action<HtmlScriptError>? _report;
    private readonly ConditionalWeakTable<INode, NodeObject> _wrappers = new();
    private readonly JsObject _window;
    private readonly JsObject _nodePrototype;
    private readonly JsObject _elementPrototype;
    private readonly JsObject _documentPrototype;

    public HtmlScriptHost(HtmlPage page, HtmlPageOptions options)
    {
        _page = page;
        _report = options.OnScriptError;
        _engine = new Engine(new EngineOptions { MaximumLoopIterations = options.MaximumLoopIterations });
        var objectPrototype = ((JsObject)_engine.GetGlobal("Object").Data!).Get("prototype").Data as JsObject;
        _nodePrototype = new JsObject { Prototype = objectPrototype };
        _elementPrototype = new JsObject { Prototype = _nodePrototype };
        _documentPrototype = new JsObject { Prototype = _nodePrototype };
        _window = (JsObject)_engine.GetGlobal("globalThis").Data!;
        BindNode();
        BindElement();
        BindDocument();
        BindListeners(_window);
        _engine.SetGlobal("window", JsValue.From(_window));
        _engine.SetGlobal("document", Wrap(page.Document));
    }

    public static bool IsInlineClassicScript(IElement script) =>
        script.NamespaceUri == "http://www.w3.org/1999/xhtml" &&
        !script.HasAttribute("src") &&
        !script.HasAttribute("nomodule") &&
        ClassicTypes.Contains(script.GetAttribute("type")?.Trim() ?? "");

    public void RunInlineScript(string text, int index)
    {
        var source = $"script[{index}]";
        Run(source, () => _engine.Evaluate(text));
        DrainJobs(source);
    }

    public void DispatchClick(IElement target)
    {
        var path = new List<JsObject>();
        for (INode? node = target; node is not null; node = node.Parent)
            path.Add(GetWrapper(node));
        if (path[^1] is not NodeObject document || !ReferenceEquals(document.Node, _page.Document))
            return;
        path.Add(_window);

        var stopped = false;
        var current = JsValue.Null;
        var evt = new JsObject { Prototype = _nodePrototype.Prototype };
        evt.DefineOwnProperty("type", PropertyDescriptor.Data(JsValue.From("click")));
        evt.DefineOwnProperty("target", PropertyDescriptor.Data(Wrap(target)));
        Accessor(evt, "currentTarget", _ => current);
        Method(evt, "stopPropagation", 0, (_, _) =>
        {
            stopped = true;
            return JsValue.Undefined;
        });
        var args = new[] { JsValue.From(evt) };

        try
        {
            foreach (var receiver in path)
            {
                current = JsValue.From(receiver);
                if (GetListeners(receiver, "click", create: false) is { } listeners)
                {
                    foreach (var entry in listeners.ToArray())
                    {
                        if (!entry.Removed)
                            Run("click", () => entry.Function.Call(current, args));
                    }
                }
                if (stopped)
                    break;
            }
            current = JsValue.Null;
            DrainJobs("click");
        }
        finally
        {
            current = JsValue.Null;
            _page.PublishChanges();
        }
    }

    private void BindNode()
    {
        Accessor(_nodePrototype, "textContent", receiver => NullableString(Node(receiver).TextContent), (receiver, value) =>
        {
            var node = Node(receiver);
            if (node is IDocument)
                return;
            node.TextContent = value.Type is JsType.Null ? "" : value.ToJsString();
            _page.MarkDirty();
        });
        Accessor(_nodePrototype, "parentNode", receiver => Wrap(Node(receiver).Parent));
        Accessor(_nodePrototype, "parentElement", receiver => Wrap(Node(receiver).ParentElement));
        Method(_nodePrototype, "appendChild", 1, (receiver, args) =>
        {
            var result = Node(receiver).AppendChild(Node(Arg(args, 0)));
            _page.MarkDirty();
            return Wrap(result);
        });
        Method(_nodePrototype, "removeChild", 1, (receiver, args) =>
        {
            var result = Node(receiver).RemoveChild(Node(Arg(args, 0)));
            _page.MarkDirty();
            return Wrap(result);
        });
        BindListeners(_nodePrototype);
    }

    private void BindElement()
    {
        Accessor(_elementPrototype, "tagName", receiver => JsValue.From(Element(receiver).TagName));
        AttributeProperty("id", "id");
        AttributeProperty("className", "class");
        Method(_elementPrototype, "querySelector", 1, (receiver, args) => Wrap(Element(receiver).QuerySelector(Arg(args, 0).ToJsString())));
        Method(_elementPrototype, "getAttribute", 1, (receiver, args) => NullableString(Element(receiver).GetAttribute(Arg(args, 0).ToJsString())));
        Method(_elementPrototype, "setAttribute", 2, (receiver, args) =>
        {
            Element(receiver).SetAttribute(Arg(args, 0).ToJsString(), Arg(args, 1).ToJsString());
            _page.MarkDirty();
            return JsValue.Undefined;
        });
        Method(_elementPrototype, "removeAttribute", 1, (receiver, args) =>
        {
            Element(receiver).RemoveAttribute(Arg(args, 0).ToJsString());
            _page.MarkDirty();
            return JsValue.Undefined;
        });
        Method(_elementPrototype, "remove", 0, (receiver, _) =>
        {
            var element = Element(receiver);
            if (element.Parent is { } parent)
            {
                parent.RemoveChild(element);
                _page.MarkDirty();
            }
            return JsValue.Undefined;
        });
    }

    private void AttributeProperty(string property, string attribute) =>
        Accessor(_elementPrototype, property, receiver => JsValue.From(Element(receiver).GetAttribute(attribute) ?? ""), (receiver, value) =>
        {
            Element(receiver).SetAttribute(attribute, value.ToJsString());
            _page.MarkDirty();
        });

    private void BindDocument()
    {
        Accessor(_documentPrototype, "body", receiver => Wrap(Document(receiver).Body));
        Accessor(_documentPrototype, "documentElement", receiver => Wrap(Document(receiver).DocumentElement));
        Accessor(_documentPrototype, "title", receiver => JsValue.From(Document(receiver).Title ?? ""), (receiver, value) =>
        {
            Document(receiver).Title = value.ToJsString();
            _page.MarkDirty();
        });
        Method(_documentPrototype, "getElementById", 1, (receiver, args) => Wrap(Document(receiver).GetElementById(Arg(args, 0).ToJsString())));
        Method(_documentPrototype, "querySelector", 1, (receiver, args) => Wrap(Document(receiver).QuerySelector(Arg(args, 0).ToJsString())));
        Method(_documentPrototype, "createElement", 1, (receiver, args) => Wrap(Document(receiver).CreateElement(Arg(args, 0).ToJsString())));
        Method(_documentPrototype, "createTextNode", 1, (receiver, args) => Wrap(Document(receiver).CreateTextNode(Arg(args, 0).ToJsString())));
    }

    private void BindListeners(JsObject target)
    {
        Method(target, "addEventListener", 2, (receiver, args) =>
        {
            if (Arg(args, 1).Data is JsFunction function)
            {
                var listeners = GetListeners(receiver.Data, Arg(args, 0).ToJsString(), create: true);
                if (listeners is not null && !listeners.Any(entry => ReferenceEquals(entry.Function, function)))
                    listeners.Add(new Listener(function));
            }
            return JsValue.Undefined;
        });
        Method(target, "removeEventListener", 2, (receiver, args) =>
        {
            if (Arg(args, 1).Data is JsFunction function &&
                GetListeners(receiver.Data, Arg(args, 0).ToJsString(), create: false) is { } listeners)
            {
                var index = listeners.FindIndex(entry => ReferenceEquals(entry.Function, function));
                if (index >= 0)
                {
                    listeners[index].Removed = true;
                    listeners.RemoveAt(index);
                }
            }
            return JsValue.Undefined;
        });
    }

    // The host currently generates click events only.
    private List<Listener>? GetListeners(object? target, string type, bool create)
    {
        if (target is not NodeObject && !ReferenceEquals(target, _window))
            throw TypeError("Expected an event target");
        if (type != "click")
            return null;

        return target is NodeObject node
            ? create ? node.ClickListeners ??= [] : node.ClickListeners
            : create ? _windowListeners ??= [] : _windowListeners;
    }

    private List<Listener>? _windowListeners;

    private void DrainJobs(string source) => Run($"{source} Promise jobs", _engine.DrainJobs);

    private void Run(string source, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (error is JsSyntaxException or InvalidOperationException)
        {
            _report?.Invoke(new HtmlScriptError(source, error));
        }
    }

    private NodeObject GetWrapper(INode node) => _wrappers.GetValue(node, CreateWrapper);

    private NodeObject CreateWrapper(INode node) => new(node)
    {
        Prototype = node switch
        {
            IDocument => _documentPrototype,
            IElement => _elementPrototype,
            _ => _nodePrototype
        }
    };

    private JsValue Wrap(INode? node) => node is null ? JsValue.Null : JsValue.From(GetWrapper(node));
    private static INode Node(JsValue receiver) => (receiver.Data as NodeObject)?.Node ?? throw TypeError("Expected a DOM node");
    private static IElement Element(JsValue receiver) => Node(receiver) as IElement ?? throw TypeError("Expected an element");
    private static IDocument Document(JsValue receiver) => Node(receiver) as IDocument ?? throw TypeError("Expected a document");
    private static JsException TypeError(string message) => new(JsValue.From(new JsError("TypeError", message)));
    private static JsValue Arg(JsValue[] args, int index) => index < args.Length ? args[index] : JsValue.Undefined;
    private static JsValue NullableString(string? value) => value is null ? JsValue.Null : JsValue.From(value);

    private static void Method(JsObject target, string name, int arity, Func<JsValue, JsValue[], JsValue> call) =>
        target.DefineOwnProperty(name, PropertyDescriptor.Data(HostFunction(call, arity), writable: true, configurable: true));

    private static void Accessor(JsObject target, string name, Func<JsValue, JsValue> get, Action<JsValue, JsValue>? set = null) =>
        target.DefineOwnProperty(name, PropertyDescriptor.Accessor(
            HostFunction((receiver, _) => get(receiver), 0),
            set is null ? null : HostFunction((receiver, args) =>
            {
                set(receiver, Arg(args, 0));
                return JsValue.Undefined;
            }, 1)));

    private static JsValue HostFunction(Func<JsValue, JsValue[], JsValue> call, int arity) =>
        JsValue.From(new JsFunction((receiver, args) =>
        {
            try
            {
                return call(receiver, args);
            }
            catch (DomException error)
            {
                throw TypeError(error.Message);
            }
        }, arity));

    private sealed class NodeObject(INode node) : JsObject
    {
        public INode Node { get; } = node;
        public List<Listener>? ClickListeners { get; set; }
    }

    private sealed class Listener(JsFunction function)
    {
        public JsFunction Function { get; } = function;
        public bool Removed { get; set; }
    }
}
