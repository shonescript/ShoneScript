using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using ShoneJs;
using ShoneJs.Parsing;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost : IDisposable
{
    private static readonly HashSet<string> ClassicTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "text/javascript", "application/javascript", "text/ecmascript", "application/ecmascript",
        "application/x-ecmascript", "application/x-javascript", "text/jscript", "text/livescript",
        "text/x-ecmascript", "text/x-javascript", "text/javascript1.0", "text/javascript1.1",
        "text/javascript1.2", "text/javascript1.3", "text/javascript1.4", "text/javascript1.5"
    };

    private readonly HtmlPage _page;
    private HtmlRendererDocument? Renderer => _page.RendererContext;
    private HtmlBrowserOptions? BrowserOptions => Renderer?.Options ?? _page.Browser?.Options;
    private string? _rendererFailure;
    private readonly Engine _engine;
    private readonly HtmlTaskQueue _tasks;
    private int _dispatchDepth;
    private bool _checkpointFailed;
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
        var engineOptions = new EngineOptions { MaximumLoopIterations = options.MaximumLoopIterations, CancellationToken = page.Lifetime };
        if (Renderer is { } renderer)
        {
            engineOptions.MaximumPendingJobs = 1024;
            engineOptions.MaximumSourceLength = Math.Min(engineOptions.MaximumSourceLength, renderer.Options.MaximumResponseBytes);
            _createdNodes = Measure(page.Document, renderer.Options.MaximumDocumentDepth, renderer.Options.MaximumDocumentNodes, includeRoot: true);
        }
        _engine = new Engine(engineOptions);
        _tasks = new HtmlTaskQueue(page.Context, page.Lifetime, RunTask);
        var objectPrototype = ((JsObject)_engine.GetGlobal("Object").Data!).Get("prototype").Data as JsObject;
        _nodePrototype = new JsObject { Prototype = objectPrototype };
        _elementPrototype = new JsObject { Prototype = _nodePrototype };
        _documentPrototype = new JsObject { Prototype = _nodePrototype };
        _window = (JsObject)_engine.GetGlobal("globalThis").Data!;
        BindNode();
        BindElement();
        BindDocument();
        BindDomProperties();
        BindListeners(_window);
        _engine.SetGlobal("window", JsValue.From(_window));
        _engine.SetGlobal("document", Wrap(page.Document));
        BindWebApis();
    }

    public static bool IsInlineClassicScript(IElement script) =>
        script.NamespaceUri == "http://www.w3.org/1999/xhtml" &&
        !script.HasAttribute("src") &&
        !script.HasAttribute("nomodule") &&
        ClassicTypes.Contains(script.GetAttribute("type")?.Trim() ?? "");

    public void RunInlineScript(string text, int index)
    {
        var source = $"script[{index}]";
        _checkpointFailed = false;
        Run(source, () => _engine.Evaluate(text));
        DrainJobs(source);
    }

    public bool DispatchClick(IElement target) => DispatchEvent(target, "click");

    public bool DispatchEvent(IElement target, string type, bool bubbles = true, bool cancelable = true) =>
        DispatchHostEvent(GetWrapper(target), NewEvent(type, bubbles, cancelable));

    public void DispatchWindowEvent(string type, string? oldUrl = null, string? newUrl = null)
    {
        var evt = NewEvent(type, false, false);
        if (oldUrl is not null) evt.DefineOwnProperty("oldURL", PropertyDescriptor.Data(JsValue.From(oldUrl)));
        if (newUrl is not null) evt.DefineOwnProperty("newURL", PropertyDescriptor.Data(JsValue.From(newUrl)));
        if (type == "popstate") evt.DefineOwnProperty("state", PropertyDescriptor.Data(ReadHistoryState()));
        DispatchHostEvent(_window, evt);
    }

    internal void PumpTasks()
    {
        ThrowIfRendererFailed();
        _tasks.Pump();
    }

    private bool PostTask(string source, Action action)
    {
        if (_tasks.Post(source, action)) return true;
        if (!_page.Lifetime.IsCancellationRequested) TerminateRenderer("Page task queue limit exceeded.");
        return false;
    }

    private void TerminateRenderer(string reason)
    {
        if (Renderer is not { } renderer || Interlocked.CompareExchange(ref _rendererFailure, reason, null) is not null) return;
        _loadCompletion?.TrySetException(new RendererBudgetException(reason));
        renderer.Abort();
    }

    private void ThrowIfRendererFailed()
    {
        if (Volatile.Read(ref _rendererFailure) is { } reason) throw new RendererBudgetException(reason);
    }

    private sealed class RendererBudgetException(string reason) : IOException(reason);

    private void RunTask(string source, Action action)
    {
        if (_page.Lifetime.IsCancellationRequested) return;
        try
        {
            _checkpointFailed = false;
            Run(source, action);
            DrainJobs(source);
            _page.PublishChanges();
        }
        catch (RendererBudgetException)
        {
            // Abort already signaled the worker loop; do not let this callback escape the owner thread.
        }
        catch (OperationCanceledException) when (Renderer is not null && _page.Lifetime.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _tasks.Dispose();
        DisposeTimers();
    }

    private void BindNode()
    {
        Accessor(_nodePrototype, "textContent", receiver => NullableString(Node(receiver).TextContent), (receiver, value) =>
        {
            var node = Node(receiver);
            if (node is IDocument)
                return;
            var text = value.Type is JsType.Null ? "" : value.ToJsString();
            if (BrowserOptions is not null && text.Length != 0)
            {
                if (Renderer is not null && node.NodeType is not (NodeType.Element or NodeType.DocumentFragment))
                    CheckDomText(text, "Text");
                else
                {
                    var textNode = CreateText(text);
                    if (node.NodeType is NodeType.Element or NodeType.DocumentFragment) CheckInsert(node, textNode);
                }
            }
            node.TextContent = text;
            _page.MarkDirty();
        });
        Accessor(_nodePrototype, "parentNode", receiver => Wrap(Node(receiver).Parent));
        Accessor(_nodePrototype, "parentElement", receiver => Wrap(Node(receiver).ParentElement));
        Method(_nodePrototype, "appendChild", 1, (receiver, args) =>
        {
            var parent = Node(receiver);
            var child = Node(Arg(args, 0));
            CheckInsert(parent, child);
            var result = parent.AppendChild(child);
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
            var name = Arg(args, 0).ToJsString();
            var value = Arg(args, 1).ToJsString();
            if (Renderer is not null)
            {
                CheckDomText(name, "Attribute name");
                CheckDomText(value, "Attribute value");
            }
            Element(receiver).SetAttribute(name, value);
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
            var text = value.ToJsString();
            if (Renderer is not null) CheckDomText(text, "Attribute value");
            Element(receiver).SetAttribute(attribute, text);
            _page.MarkDirty();
        });

    private void BindDocument()
    {
        Accessor(_documentPrototype, "body", receiver => Wrap(Document(receiver).Body));
        Accessor(_documentPrototype, "documentElement", receiver => Wrap(Document(receiver).DocumentElement));
        Accessor(_documentPrototype, "title", receiver => JsValue.From(Document(receiver).Title ?? ""), (receiver, value) =>
        {
            var text = value.ToJsString();
            if (Renderer is not null) CheckDomText(text, "Title");
            Document(receiver).Title = text;
            _page.MarkDirty();
        });
        Method(_documentPrototype, "getElementById", 1, (receiver, args) => Wrap(Document(receiver).GetElementById(Arg(args, 0).ToJsString())));
        Method(_documentPrototype, "querySelector", 1, (receiver, args) => Wrap(Document(receiver).QuerySelector(Arg(args, 0).ToJsString())));
        Method(_documentPrototype, "createElement", 1, (receiver, args) => Wrap(AccountNode(Document(receiver).CreateElement(Arg(args, 0).ToJsString()))));
        Method(_documentPrototype, "createTextNode", 1, (receiver, args) => Wrap(CreateText(Arg(args, 0).ToJsString())));
    }

    private void DrainJobs(string source)
    {
        ThrowIfRendererFailed();
        if (_checkpointFailed) return;
        var success = false;
        Run($"{source} Promise jobs", () => { _engine.DrainJobs(); success = true; });
        _checkpointFailed = !success;
        if (Renderer is { } renderer && !_page.Lifetime.IsCancellationRequested)
            Measure(_page.Document, renderer.Options.MaximumDocumentDepth, renderer.Options.MaximumDocumentNodes, includeRoot: true);
        if (!success && !_page.Lifetime.IsCancellationRequested && _loadCompletion is { Task.IsCompleted: false } completion)
            completion.TrySetException(new InvalidOperationException($"Promise checkpoint failed during {source}."));
    }

    private void Run(string source, Action action)
    {
        ThrowIfRendererFailed();
        try
        {
            if (!_page.Lifetime.IsCancellationRequested) action();
        }
        catch (OperationCanceledException) when (_page.Lifetime.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException error) when (Renderer is not null && IsEngineBudgetError(error))
        {
            throw BudgetError(error.Message);
        }
        catch (Exception error) when (error is JsSyntaxException or InvalidOperationException)
        {
            _report?.Invoke(new HtmlScriptError(source, error));
        }
        ThrowIfRendererFailed();
    }

    private static bool IsEngineBudgetError(InvalidOperationException error) => error is not JsException && error.Message is
        "Execution limit exceeded" or "Execution step limit exceeded" or "Execution nesting limit exceeded" or "Call depth limit exceeded" or
        "Microtask queue limit exceeded" or "Promise reaction limit exceeded" or "Source length limit exceeded" or "Page task queue limit exceeded.";

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
    }
}
