using AngleSharp.Dom;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private void BindDomTree()
    {
        Constructor("Node", 0, _ => throw TypeError("Illegal constructor"), _nodePrototype);
        Constructor("Element", 0, _ => throw TypeError("Illegal constructor"), _elementPrototype);
        Constructor("HTMLElement", 0, _ => throw TypeError("Illegal constructor"), _htmlElementPrototype);
        Constructor("Document", 0, _ => throw TypeError("Illegal constructor"), _documentPrototype);
        Constructor("DocumentFragment", 0, _ => Wrap(AccountNode(_page.Document.CreateDocumentFragment())), _fragmentPrototype);

        Accessor(_fragmentPrototype, "children", e => NodeList(() => Fragment(e).Children));
        Accessor(_fragmentPrototype, "childElementCount", e => JsValue.From((double)Fragment(e).Children.Length));
        Accessor(_fragmentPrototype, "firstElementChild", e => Wrap(Fragment(e).Children.FirstOrDefault()));
        Accessor(_fragmentPrototype, "lastElementChild", e => Wrap(Fragment(e).Children.LastOrDefault()));
        Method(_fragmentPrototype, "getElementById", 1, (e, a) => Wrap(Fragment(e).GetElementById(Arg(a, 0).ToJsString())));
        Method(_fragmentPrototype, "querySelector", 1, (e, a) => Wrap(Fragment(e).QuerySelector(Arg(a, 0).ToJsString())));
        Method(_fragmentPrototype, "querySelectorAll", 1, (e, a) =>
        {
            var matches = Fragment(e).QuerySelectorAll(Arg(a, 0).ToJsString()).ToArray();
            return NodeList(() => matches);
        });
        foreach (var prototype in new[] { _elementPrototype, _fragmentPrototype })
        {
            Method(prototype, "prepend", 0, (e, a) => InsertChildren(Node(e), a, prepend: true));
            Method(prototype, "append", 0, (e, a) => InsertChildren(Node(e), a, prepend: false));
        }
        Method(_elementPrototype, "getHTML", 0, (e, _) => JsValue.From(Element(e).InnerHtml));
        Method(_elementPrototype, "toggleAttribute", 1, (e, a) =>
        {
            var element = Element(e);
            if (a.Length == 0) throw TypeError("toggleAttribute requires an attribute name");
            var name = a[0].ToJsString();
            if (!IsDomName(name)) throw new JsException(Error("InvalidCharacterError", "Invalid attribute name"));
            CheckDomText(name, "Attribute name");
            var had = element.HasAttribute(name);
            var force = a.Length == 1 ? !had : a[1].ToBoolean();
            if (force == had) return JsValue.From(had);
            if (force) element.SetAttribute(name, ""); else element.RemoveAttribute(name);
            ResetCanvasDimension(element, name);
            if (element is AngleSharp.Html.Dom.IHtmlImageElement image && name.Equals("src", StringComparison.OrdinalIgnoreCase))
                UpdateImageSource(image);
            _page.MarkDirty();
            return JsValue.From(force);
        });
        BindTraversal();
        BindDomSerialization();
    }

    private static bool IsDomName(string text)
    {
        try { System.Xml.XmlConvert.VerifyName(text); return true; }
        catch (System.Xml.XmlException) { return false; }
    }

    private static IDocumentFragment Fragment(JsValue value) => Node(value) as IDocumentFragment ?? throw TypeError("Expected a document fragment");

    private JsValue InsertChildren(INode parent, JsValue[] args, bool prepend)
    {
        if (parent is not (IDocumentFragment or IElement)) throw TypeError("Expected a parent node");
        if (args.Length == 0) return JsValue.Undefined;
        var children = args.Select(arg => arg.Data is NodeObject node ? node.Node : CreateText(arg.ToJsString())).ToArray();
        foreach (var child in children)
        {
            if (child is IDocument or IDocumentType) throw TypeError("Invalid child node");
            for (INode? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
                if (ReferenceEquals(ancestor, child)) throw TypeError("A child cannot contain its parent");
            CheckInsert(parent, child);
        }
        // Convert the arguments to a single node first, so moving an existing first child
        // does not change which nodes come after the inserted sequence.
        var fragment = _page.Document.CreateDocumentFragment();
        foreach (var child in children) fragment.AppendChild(child);
        parent.InsertBefore(fragment, prepend ? parent.FirstChild : null);
        _page.MarkDirty();
        return JsValue.Undefined;
    }

    private void BindTraversal()
    {
        var walker = NewObject();
        var iterator = NewObject();
        foreach (var (name, prototype) in new[] { ("TreeWalker", walker), ("NodeIterator", iterator) })
        {
            Constructor(name, 0, _ => throw TypeError("Illegal constructor"), prototype);
            Accessor(prototype, "root", e => Wrap(Cursor(e).Root));
            Accessor(prototype, "whatToShow", e => JsValue.From((double)Cursor(e).Mask));
            Accessor(prototype, "filter", e => Cursor(e).Filter);
        }
        Accessor(walker, "currentNode", e => Wrap(Walker(e).Current), (e, v) => Walker(e).Current = Node(v));
        Method(walker, "nextNode", 0, (e, _) => Wrap(Walker(e).ToNext()));
        Method(walker, "previousNode", 0, (e, _) => Wrap(Walker(e).ToPrevious()));
        Method(walker, "parentNode", 0, (e, _) => Wrap(Walker(e).ToParent()));
        Method(walker, "firstChild", 0, (e, _) => Wrap(Walker(e).ToFirst()));
        Method(walker, "lastChild", 0, (e, _) => Wrap(Walker(e).ToLast()));
        Method(walker, "nextSibling", 0, (e, _) => Wrap(Walker(e).ToNextSibling()));
        Method(walker, "previousSibling", 0, (e, _) => Wrap(Walker(e).ToPreviousSibling()));
        Accessor(iterator, "referenceNode", e => Wrap(Iterator(e).Reference));
        Accessor(iterator, "pointerBeforeReferenceNode", e => JsValue.From(Iterator(e).IsBeforeReference));
        Method(iterator, "nextNode", 0, (e, _) => Wrap(Iterator(e).Next()));
        Method(iterator, "previousNode", 0, (e, _) => Wrap(Iterator(e).Previous()));
        Method(iterator, "detach", 0, (e, _) => { Iterator(e); return JsValue.Undefined; });
        Method(_documentPrototype, "createTreeWalker", 1, (e, a) => CreateCursor(e, a, walker, tree: true));
        Method(_documentPrototype, "createNodeIterator", 1, (e, a) => CreateCursor(e, a, iterator, tree: false));

        var constants = NewObject();
        foreach (var (name, value) in new (string, uint)[] {
            ("FILTER_ACCEPT", 1), ("FILTER_REJECT", 2), ("FILTER_SKIP", 3), ("SHOW_ALL", uint.MaxValue),
            ("SHOW_ELEMENT", 1), ("SHOW_ATTRIBUTE", 2), ("SHOW_TEXT", 4), ("SHOW_CDATA_SECTION", 8),
            ("SHOW_ENTITY_REFERENCE", 16), ("SHOW_ENTITY", 32), ("SHOW_PROCESSING_INSTRUCTION", 64),
            ("SHOW_COMMENT", 128), ("SHOW_DOCUMENT", 256), ("SHOW_DOCUMENT_TYPE", 512),
            ("SHOW_DOCUMENT_FRAGMENT", 1024), ("SHOW_NOTATION", 2048) })
            constants.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.From((double)value), enumerable: true));
        _engine.SetGlobal("NodeFilter", JsValue.From(constants));
    }

    private JsValue CreateCursor(JsValue receiver, JsValue[] args, JsObject prototype, bool tree)
    {
        var doc = Document(receiver);
        var root = Node(Arg(args, 0));
        var mask = Arg(args, 1).Type == JsType.Undefined ? uint.MaxValue : DomUnsignedLong(args[1]);
        var filter = Arg(args, 2);
        if (filter.Type is JsType.Null or JsType.Undefined) filter = JsValue.Null;
        else if (filter.Data is not JsObject) throw TypeError("Expected a node filter");
        var active = false;
        FilterResult Apply(INode node)
        {
            if (active) throw new JsException(Error("InvalidStateError", "A node filter cannot recursively call its traversal"));
            active = true;
            try
            {
                var method = filter.Data as JsFunction ?? ((JsObject)filter.Data!).Get("acceptNode").Data as JsFunction
                    ?? throw TypeError("Expected an acceptNode callback");
                return (ushort)DomUnsignedLong(_engine.Invoke(method, filter.Data is JsFunction ? JsValue.Undefined : filter, [Wrap(node)])) switch
                {
                    1 => FilterResult.Accept,
                    2 when tree => FilterResult.Reject,
                    _ => FilterResult.Skip
                };
            }
            finally { active = false; }
        }
        NodeFilter? callback = filter.Type == JsType.Null ? null : Apply;
        object traversal = tree ? doc.CreateTreeWalker(root, (FilterSettings)mask, callback) : doc.CreateNodeIterator(root, (FilterSettings)mask, callback);
        return JsValue.From(new DomCursor(root, mask, filter, traversal) { Prototype = prototype });
    }

    private static uint DomUnsignedLong(JsValue value)
    {
        var number = value.ToNumber();
        return double.IsFinite(number) ? (uint)((Math.Truncate(number) % 4294967296d + 4294967296d) % 4294967296d) : 0;
    }
    private static DomCursor Cursor(JsValue e) => e.Data as DomCursor ?? throw TypeError("Expected a DOM traversal object");
    private static ITreeWalker Walker(JsValue e) => Cursor(e).Traversal as ITreeWalker ?? throw TypeError("Expected a TreeWalker");
    private static INodeIterator Iterator(JsValue e) => Cursor(e).Traversal as INodeIterator ?? throw TypeError("Expected a NodeIterator");
    private sealed class DomCursor(INode root, uint mask, JsValue filter, object traversal) : JsObject
    {
        public INode Root { get; } = root;
        public uint Mask { get; } = mask;
        public JsValue Filter { get; } = filter;
        public object Traversal { get; } = traversal;
    }
}
