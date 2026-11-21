using System.Globalization;
using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private readonly ConditionalWeakTable<IElement, ElementProperties> _properties = new();
    private string _readyState = "loading";

    private JsObject NewObject() => new() { Prototype = _nodePrototype.Prototype };
    private JsArray NewArray() => new() { Prototype = (_engine.GetGlobal("Array").Data as JsObject)?.Get("prototype").Data as JsObject };
    private JsValue NodeList(Func<IEnumerable<INode>> nodes) => JsValue.From(new DomCollection(this, nodes));

    private void BindDomProperties()
    {
        Accessor(_nodePrototype, "nodeType", e => JsValue.From((double)Node(e).NodeType));
        Accessor(_nodePrototype, "nodeName", e => JsValue.From(Node(e).NodeName));
        Accessor(_nodePrototype, "nodeValue", e => NullableString(Node(e).NodeValue), (e, v) =>
        {
            Node(e).NodeValue = v.Type == JsType.Null ? "" : v.ToJsString();
            _page.MarkDirty();
        });
        Accessor(_nodePrototype, "ownerDocument", e => Node(e) is IDocument ? JsValue.Null : Wrap(_page.Document));
        Accessor(_nodePrototype, "firstChild", e => Wrap(Node(e).FirstChild));
        Accessor(_nodePrototype, "lastChild", e => Wrap(Node(e).LastChild));
        Accessor(_nodePrototype, "nextSibling", e => Wrap(Node(e).NextSibling));
        Accessor(_nodePrototype, "previousSibling", e => Wrap(Node(e).PreviousSibling));
        Accessor(_nodePrototype, "childNodes", e => NodeList(() => Node(e).ChildNodes));
        Accessor(_nodePrototype, "isConnected", e =>
        {
            for (INode? node = Node(e); node is not null; node = node.Parent)
                if (ReferenceEquals(node, _page.Document)) return JsValue.From(true);
            return JsValue.From(false);
        });
        Method(_nodePrototype, "contains", 1, (e, a) =>
        {
            if (Arg(a, 0).Type == JsType.Null) return JsValue.From(false);
            var ancestor = Node(e);
            for (INode? node = Node(Arg(a, 0)); node is not null; node = node.Parent)
                if (ReferenceEquals(node, ancestor)) return JsValue.From(true);
            return JsValue.From(false);
        });
        Method(_nodePrototype, "insertBefore", 2, (e, a) =>
        {
            var child = Node(Arg(a, 0));
            CheckInsert(Node(e), child);
            Node(e).InsertBefore(child, Arg(a, 1).Type is JsType.Null or JsType.Undefined ? null : Node(Arg(a, 1)));
            _page.MarkDirty();
            return Wrap(child);
        });
        Method(_nodePrototype, "replaceChild", 2, (e, a) =>
        {
            var old = Node(Arg(a, 1));
            var child = Node(Arg(a, 0));
            CheckInsert(Node(e), child);
            Node(e).ReplaceChild(child, old);
            _page.MarkDirty();
            return Wrap(old);
        });
        Method(_nodePrototype, "cloneNode", 1, (e, a) => Wrap(AccountNode(Node(e).Clone(Arg(a, 0).ToBoolean()))));
        Accessor(_elementPrototype, "localName", e => JsValue.From(Element(e).LocalName));
        Accessor(_elementPrototype, "namespaceURI", e => NullableString(Element(e).NamespaceUri));
        Accessor(_elementPrototype, "children", e => NodeList(() => Element(e).Children));
        Accessor(_elementPrototype, "childElementCount", e => JsValue.From((double)Element(e).Children.Length));
        Accessor(_elementPrototype, "firstElementChild", e => Wrap(Element(e).Children.FirstOrDefault()));
        Accessor(_elementPrototype, "lastElementChild", e => Wrap(Element(e).Children.LastOrDefault()));
        Accessor(_elementPrototype, "nextElementSibling", e => Wrap(Element(e).NextElementSibling));
        Accessor(_elementPrototype, "previousElementSibling", e => Wrap(Element(e).PreviousElementSibling));
        Accessor(_elementPrototype, "innerHTML", e => JsValue.From(Element(e).InnerHtml), (e, v) =>
        {
            SetInnerHtml(Element(e), v.Type == JsType.Null ? "" : v.ToJsString());
            _page.MarkDirty();
        });
        Accessor(_elementPrototype, "outerHTML", e => JsValue.From(Element(e).OuterHtml),
            (e, v) => SetOuterHtml(Element(e), v.Type == JsType.Null ? "" : v.ToJsString()));
        Accessor(_elementPrototype, "hidden", e => JsValue.From(Element(e).HasAttribute("hidden")),
            (e, v) => SetBooleanAttribute(Element(e), "hidden", v));
        Accessor(_elementPrototype, "open", e => Element(e) is IHtmlDetailsElement details
            ? JsValue.From(details.HasAttribute("open")) : JsValue.Undefined, (e, v) =>
        {
            if (Element(e) is IHtmlDetailsElement details) SetBooleanAttribute(details, "open", v);
        });
        Accessor(_elementPrototype, "classList", e => JsValue.From(Properties(Element(e)).Classes));
        Accessor(_elementPrototype, "dataset", e => JsValue.From(Properties(Element(e)).Dataset));
        Accessor(_elementPrototype, "style", e => JsValue.From(Properties(Element(e)).Style), (e, v) => SetStyle(Element(e), v.ToJsString()));
        Method(_elementPrototype, "matches", 1, (e, a) => JsValue.From(Element(e).Matches(Arg(a, 0).ToJsString())));
        Method(_elementPrototype, "closest", 1, (e, a) => Wrap(Element(e).Closest(Arg(a, 0).ToJsString())));
        Method(_elementPrototype, "hasAttribute", 1, (e, a) => JsValue.From(Element(e).HasAttribute(Arg(a, 0).ToJsString())));
        Method(_elementPrototype, "querySelectorAll", 1, (e, a) =>
        {
            var matches = Element(e).QuerySelectorAll(Arg(a, 0).ToJsString()).ToArray();
            return NodeList(() => matches);
        });
        Method(_elementPrototype, "replaceWith", 0, (e, a) =>
        {
            ReplaceWith(Element(e), a);
            return JsValue.Undefined;
        });
        Method(_elementPrototype, "append", 0, (e, a) =>
        {
            var node = Element(e);
            foreach (var item in a)
            {
                var child = item.Data is NodeObject wrapper ? wrapper.Node : CreateText(item.ToJsString());
                CheckInsert(node, child);
                node.AppendChild(child);
            }
            _page.MarkDirty();
            return JsValue.Undefined;
        });
        Method(_elementPrototype, "focus", 0, (e, _) => { _page.SetFocusedElement(Element(e)); return JsValue.Undefined; });
        Method(_elementPrototype, "blur", 0, (e, _) =>
        {
            if (ReferenceEquals(Element(e), _page.FocusedElement)) _page.SetFocusedElement(null);
            return JsValue.Undefined;
        });
        Accessor(_documentPrototype, "head", e => Wrap(Document(e).Head));
        Accessor(_documentPrototype, "defaultView", _ => JsValue.From(_window));
        Accessor(_documentPrototype, "activeElement", e => Wrap(_page.FocusedElement ?? Document(e).Body));
        Accessor(_documentPrototype, "readyState", _ => JsValue.From(_readyState));
        Accessor(_documentPrototype, "URL", _ => JsValue.From(_page.Address?.AbsoluteUri ?? "about:blank"));
        Accessor(_documentPrototype, "baseURI", _ => JsValue.From(_page.BaseAddress?.AbsoluteUri ?? "about:blank"));
        Method(_documentPrototype, "querySelectorAll", 1, (e, a) =>
        {
            var matches = Document(e).QuerySelectorAll(Arg(a, 0).ToJsString()).ToArray();
            return NodeList(() => matches);
        });
        Method(_documentPrototype, "createDocumentFragment", 0, (e, _) => Wrap(AccountNode(Document(e).CreateDocumentFragment())));
        Method(_documentPrototype, "createElementNS", 2, (e, a) => Wrap(AccountNode(Document(e).CreateElement(Arg(a, 1).ToJsString(), Arg(a, 0).ToJsString()))));
        BindFormProperties();
        BindImage();
    }

    private void BindFormProperties()
    {
        foreach (var name in new[] { "name", "placeholder", "htmlFor" }) AttributeProperty(name, name == "htmlFor" ? "for" : name);
        foreach (var name in new[] { "disabled", "required", "multiple", "readOnly" })
        {
            var attribute = name.ToLowerInvariant();
            Accessor(_elementPrototype, name, e => JsValue.From(Element(e).HasAttribute(attribute)), (e, v) => SetBooleanAttribute(Element(e), attribute, v));
        }
        Accessor(_elementPrototype, "type", e => Element(e) switch
        {
            IHtmlInputElement input => JsValue.From(input.Type),
            IHtmlButtonElement button => JsValue.From(button.Type),
            _ => JsValue.From(Element(e).GetAttribute("type") ?? "")
        }, (e, v) => { Element(e).SetAttribute("type", v.ToJsString()); _page.MarkDirty(); });
        Accessor(_elementPrototype, "value", e => NullableString(Element(e) switch
        {
            IHtmlInputElement input => input.Value,
            IHtmlSelectElement select => select.Value ?? "",
            IHtmlTextAreaElement textarea => textarea.Value,
            IHtmlButtonElement button => button.Value,
            IHtmlOptionElement option => option.Value,
            var element => element.GetAttribute("value") ?? ""
        }), (e, v) =>
        {
            var value = v.Type == JsType.Null ? "" : v.ToJsString();
            switch (Element(e))
            {
                case IHtmlInputElement input: input.Value = value; break;
                case IHtmlSelectElement select: select.Value = value; break;
                case IHtmlTextAreaElement textarea: textarea.Value = value; break;
                case IHtmlButtonElement button: button.Value = value; break;
                case IHtmlOptionElement option: option.Value = value; break;
                case var element: element.SetAttribute("value", value); break;
            }
            _page.MarkDirty();
        });
        Accessor(_elementPrototype, "checked", e => JsValue.From(Element(e) is IHtmlInputElement { IsChecked: true }), (e, v) =>
        {
            if (Element(e) is IHtmlInputElement input) { input.IsChecked = v.ToBoolean(); _page.MarkDirty(); }
        });
        Accessor(_elementPrototype, "selected", e => JsValue.From(Element(e) is IHtmlOptionElement { IsSelected: true }), (e, v) =>
        {
            if (Element(e) is IHtmlOptionElement option) { option.IsSelected = v.ToBoolean(); _page.MarkDirty(); }
        });
        Accessor(_elementPrototype, "selectedIndex", e => Element(e) is IHtmlSelectElement select ? JsValue.From((double)select.SelectedIndex) : JsValue.Undefined, (e, v) =>
        {
            if (Element(e) is IHtmlSelectElement select)
            {
                var selected = (int)v.ToNumber();
                for (var i = 0; i < select.Options.Length; i++) select.Options[i].IsSelected = i == selected;
                _page.MarkDirty();
            }
        });
        Accessor(_elementPrototype, "options", e => Element(e) is IHtmlSelectElement select ? NodeList(() => select.Options) : JsValue.Undefined);
        Accessor(_elementPrototype, "elements", e => Element(e) is IHtmlFormElement form ? NodeList(() => form.Elements) : JsValue.Undefined);
        Method(_elementPrototype, "submit", 0, (e, args) => { if (Element(e) is IHtmlFormElement form) _ = _page.SubmitFormAsync(form, dispatch: false); return JsValue.Undefined; });
        Method(_elementPrototype, "requestSubmit", 0, (e, a) => { if (Element(e) is IHtmlFormElement form) _ = _page.SubmitFormAsync(form, Arg(a, 0).Data is NodeObject target ? target.Node as IElement : null); return JsValue.Undefined; });
        Method(_elementPrototype, "reset", 0, (e, _) => { if (Element(e) is IHtmlFormElement form) _page.ResetForm(form); return JsValue.Undefined; });
    }

    private void SetBooleanAttribute(IElement element, string attribute, JsValue value)
    {
        if (value.ToBoolean()) element.SetAttribute(attribute, ""); else element.RemoveAttribute(attribute);
        _page.MarkDirty();
    }

    private ElementProperties Properties(IElement element) => _properties.GetValue(element, e =>
    {
        var classes = NewObject();
        Accessor(classes, "length", _ => JsValue.From((double)e.ClassList.Length));
        Accessor(classes, "value", _ => JsValue.From(e.ClassName ?? ""), (_, v) => { e.ClassName = v.ToJsString(); _page.MarkDirty(); });
        Method(classes, "item", 1, (_, a) => NullableString(e.ClassList.ElementAtOrDefault((int)Arg(a, 0).ToNumber())));
        Method(classes, "contains", 1, (_, a) => JsValue.From(e.ClassList.Contains(Arg(a, 0).ToJsString())));
        Method(classes, "add", 0, (_, a) => { e.ClassList.Add(a.Select(Token).ToArray()); _page.MarkDirty(); return JsValue.Undefined; });
        Method(classes, "remove", 0, (_, a) => { e.ClassList.Remove(a.Select(Token).ToArray()); _page.MarkDirty(); return JsValue.Undefined; });
        Method(classes, "toggle", 1, (_, a) =>
        {
            var token = Token(Arg(a, 0));
            var present = a.Length > 1 ? a[1].ToBoolean() : !e.ClassList.Contains(token);
            e.ClassList.Toggle(token, present);
            _page.MarkDirty();
            return JsValue.From(present);
        });
        Method(classes, "replace", 2, (_, a) =>
        {
            var old = Token(Arg(a, 0));
            var next = Token(Arg(a, 1));
            if (!e.ClassList.Contains(old)) return JsValue.From(false);
            var hasNext = e.ClassList.Contains(next);
            if (old != next) e.ClassName = string.Join(' ', e.ClassList.SelectMany(token => token != old ? new[] { token } : hasNext ? [] : new[] { next }));
            _page.MarkDirty();
            return JsValue.From(true);
        });
        Method(classes, "toString", 0, (_, _) => JsValue.From(e.ClassName ?? ""));
        var dataset = new DatasetObject(this, e) { Prototype = _nodePrototype.Prototype };
        var style = new InlineStyleObject(this, e) { Prototype = _nodePrototype.Prototype };
        Accessor(style, "cssText", _ => JsValue.From(e.GetAttribute("style") ?? ""), (_, v) => SetStyle(e, v.ToJsString()));
        Method(style, "getPropertyValue", 1, (_, a) => JsValue.From(style.GetValue(Arg(a, 0).ToJsString())));
        Method(style, "setProperty", 2, (_, a) => { style.SetValue(Arg(a, 0).ToJsString(), Arg(a, 1).ToJsString(), a.Length > 2 ? a[2].ToJsString() : ""); return JsValue.Undefined; });
        Method(style, "removeProperty", 1, (_, a) =>
        {
            var name = Arg(a, 0).ToJsString();
            var previous = style.GetValue(name);
            style.SetValue(name, "", "");
            return JsValue.From(previous);
        });
        return new ElementProperties(classes, dataset, style);
    });

    private static string Token(JsValue value)
    {
        var token = value.ToJsString();
        return token.Length == 0 || token.IndexOfAny([' ', '\t', '\f', '\r', '\n']) >= 0 ? throw TypeError("Invalid class token") : token;
    }

    private void SetStyle(IElement element, string value)
    {
        element.SetAttribute("style", value);
        _page.MarkDirty();
    }

    private sealed record ElementProperties(JsObject Classes, JsObject Dataset, JsObject Style);

    private sealed class DomCollection : JsObject
    {
        private readonly HtmlScriptHost _host;
        private readonly Func<IEnumerable<INode>> _nodes;

        public DomCollection(HtmlScriptHost host, Func<IEnumerable<INode>> nodes)
        {
            _host = host; _nodes = nodes; Prototype = host._nodePrototype.Prototype;
            Accessor(this, "length", _ => JsValue.From((double)_nodes().Count()));
            Method(this, "item", 1, (_, a) => host.Wrap(_nodes().ElementAtOrDefault((int)Arg(a, 0).ToNumber())));
            Method(this, "forEach", 1, (_, a) =>
            {
                if (Arg(a, 0).Data is not JsFunction callback) throw TypeError("Expected a callback");
                int i = 0;
                foreach (var node in _nodes().ToArray()) callback.Call(Arg(a, 1), [host.Wrap(node), JsValue.From((double)i++), JsValue.From(this)]);
                return JsValue.Undefined;
            });
        }

        public override JsValue Get(string key, JsValue receiver) => int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? _nodes().ElementAtOrDefault(index) is { } node ? _host.Wrap(node) : JsValue.Undefined : base.Get(key, receiver);
    }

    private sealed class DatasetObject(HtmlScriptHost host, IElement element) : JsObject
    {
        public override PropertyDescriptor? GetOwnProperty(string key) => base.GetOwnProperty(key) ??
            (element.GetAttribute(DataName(key)) is { } value ? PropertyDescriptor.Data(JsValue.From(value), writable: true, configurable: true, enumerable: true) : null);

        public override IEnumerable<string> OwnPropertyKeys() => base.OwnPropertyKeys().Concat(element.Attributes
            .Where(a => a.Name.StartsWith("data-", StringComparison.Ordinal)).Select(a => Camel(a.Name[5..]))).Distinct(StringComparer.Ordinal);

        public override bool Set(string key, JsValue value, JsValue receiver)
        {
            if (Enumerable.Range(0, key.Length - Math.Min(1, key.Length)).Any(i => key[i] == '-' && key[i + 1] is >= 'a' and <= 'z'))
                throw TypeError("Invalid dataset name");
            element.SetAttribute(DataName(key), value.ToJsString());
            host._page.MarkDirty();
            return true;
        }

        public override bool Delete(string key)
        {
            element.RemoveAttribute(DataName(key));
            host._page.MarkDirty();
            return true;
        }

        private static string DataName(string key) => "data-" + Keb(key);
    }

    private sealed class InlineStyleObject(HtmlScriptHost host, IElement element) : JsObject
    {
        public override JsValue Get(string key, JsValue receiver) => HasProperty(key) ? base.Get(key, receiver) : JsValue.From(GetValue(Keb(key)));

        public override bool Set(string key, JsValue value, JsValue receiver)
        {
            if (HasProperty(key)) return base.Set(key, value, receiver);
            SetValue(Keb(key), value.Type == JsType.Null ? "" : value.ToJsString(), "");
            return true;
        }

        public string GetValue(string name) => Read().TryGetValue(Normalize(name), out var value) ? value.Value : "";

        public void SetValue(string name, string value, string priority)
        {
            name = Normalize(name);
            if (name.Length == 0 || priority.Length != 0 && !priority.Equals("important", StringComparison.OrdinalIgnoreCase)) return;
            var values = Read();
            if (value.Length == 0) values.Remove(name); else values[name] = (value, priority.Length != 0);
            host.SetStyle(element, string.Join(' ', values.Select(pair => $"{pair.Key}: {pair.Value.Value}{(pair.Value.Important ? " !important" : "")};")));
        }

        private static string Normalize(string name) => name.StartsWith("--", StringComparison.Ordinal) ? name : name.ToLowerInvariant();

        private Dictionary<string, (string Value, bool Important)> Read()
        {
            var values = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            var css = element.GetAttribute("style") ?? "";
            var start = 0;
            var nesting = 0;
            char quote = '\0';
            for (var i = 0; i <= css.Length; i++)
            {
                var ch = i < css.Length ? css[i] : ';';
                if (i < css.Length && ch == '\\') { i++; continue; }
                if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
                if (ch is '\'' or '"') { quote = ch; continue; }
                if (ch == '(') nesting++;
                if (ch == ')' && nesting > 0) nesting--;
                if (ch != ';' || nesting != 0) continue;
                var declaration = css.AsSpan(start, i - start);
                start = i + 1;
                var colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                var value = declaration[(colon + 1)..].Trim();
                var important = value.EndsWith("!important", StringComparison.OrdinalIgnoreCase);
                if (important) value = value[..^10].TrimEnd();
                values[Normalize(declaration[..colon].Trim().ToString())] = (value.ToString(), important);
            }
            return values;
        }
    }

    private static string Keb(string key) => string.Concat(key.Select(c => c is >= 'A' and <= 'Z' ? "-" + char.ToLowerInvariant(c) : c.ToString()));
    private static string Camel(string key)
    {
        var result = new System.Text.StringBuilder();
        for (var i = 0; i < key.Length; i++)
            result.Append(key[i] == '-' && i + 1 < key.Length && key[i + 1] is >= 'a' and <= 'z' ? char.ToUpperInvariant(key[++i]) : key[i]);
        return result.ToString();
    }
}
