using System.Runtime.CompilerServices;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private readonly Dictionary<string, JsObject> _htmlPrototypes = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<IElement, JsObject> _validityObjects = new();
    private readonly ConditionalWeakTable<IElement, JsObject> _relLists = new();

    private void BindHtmlElementSemantics()
    {
        var input = RegisterHtmlElement("input", "HTMLInputElement");
        var form = RegisterHtmlElement("form", "HTMLFormElement");
        var template = RegisterHtmlElement("template", "HTMLTemplateElement");
        var datalist = RegisterHtmlElement("datalist", "HTMLDataListElement");
        var script = RegisterHtmlElement("script", "HTMLScriptElement");
        var anchor = RegisterHtmlElement("a", "HTMLAnchorElement");
        var select = RegisterHtmlElement("select", "HTMLSelectElement");
        var textarea = RegisterHtmlElement("textarea", "HTMLTextAreaElement");
        var button = RegisterHtmlElement("button", "HTMLButtonElement");
        var output = RegisterHtmlElement("output", "HTMLOutputElement");
        var fieldset = RegisterHtmlElement("fieldset", "HTMLFieldSetElement");
        foreach (var (tag, name) in new (string, string)[] {
            ("dialog", "HTMLDialogElement"), ("meter", "HTMLMeterElement"), ("progress", "HTMLProgressElement"),
            ("slot", "HTMLSlotElement"), ("picture", "HTMLPictureElement"), ("track", "HTMLTrackElement"),
            ("source", "HTMLSourceElement"), ("search", "HTMLElement"), ("menu", "HTMLMenuElement"),
            ("hgroup", "HTMLElement"), ("mark", "HTMLElement"), ("time", "HTMLTimeElement"), ("ruby", "HTMLElement"),
            ("wbr", "HTMLElement"), ("details", "HTMLDetailsElement"), ("summary", "HTMLElement"),
            ("link", "HTMLLinkElement"), ("iframe", "HTMLIFrameElement"), ("label", "HTMLLabelElement"),
            ("option", "HTMLOptionElement"), ("optgroup", "HTMLOptGroupElement") })
            if (!_htmlPrototypes.ContainsKey(tag)) RegisterHtmlElement(tag, name);
        foreach (var prototype in new[] { input, select, textarea, button, output, fieldset }) BindValidation(prototype);

        Accessor(template, "content", e => Wrap((Element(e) as IHtmlTemplateElement ?? throw TypeError("Expected a template")).Content));
        Accessor(datalist, "options", e => NodeList(() => (Element(e) as IHtmlDataListElement ?? throw TypeError("Expected a datalist")).Options));
        Accessor(input, "list", e =>
        {
            var element = Element(e);
            if (element.GetAttribute("list") is not { Length: > 0 } id) return JsValue.Null;
            return Wrap(_page.Document.GetElementById(id));
        });
        foreach (var (property, attribute) in new[] { ("autocomplete", "autocomplete"), ("dirName", "dirname"), ("pattern", "pattern") })
            ReflectString(input, property, attribute);
        Accessor(input, "formAction", e =>
        {
            var element = Element(e);
            var text = element.GetAttribute("formaction");
            return JsValue.From(text is null or "" ? _page.Address?.AbsoluteUri ?? "" : _page.ResolveAddress(text)?.AbsoluteUri ?? text);
        }, (e, v) => SetReflectedString(Element(e), "formaction", v.ToJsString()));
        ReflectBoolean(_htmlElementPrototype, "autofocus", "autofocus");
        ReflectBoolean(_htmlElementPrototype, "inert", "inert");
        ReflectString(_htmlElementPrototype, "popover", "popover");
        ReflectBoolean(_htmlElementPrototype, "hidden", "hidden");
        Method(_htmlElementPrototype, "showPopover", 0, (e, _) => SetPopover(Element(e), true));
        Method(_htmlElementPrototype, "hidePopover", 0, (e, _) => SetPopover(Element(e), false));
        Method(_htmlElementPrototype, "togglePopover", 0, (e, a) =>
        {
            var element = Element(e);
            var open = element.HasAttribute("popover") && element.HasAttribute("open");
            var next = a.Length > 0 && a[0].Type != JsType.Undefined ? a[0].ToBoolean() : !open;
            SetPopover(element, next);
            return JsValue.From(next);
        });
        _htmlElementPrototype.DefineOwnProperty("onbeforematch", PropertyDescriptor.Data(JsValue.Null, writable: true, configurable: true));
        ReflectBoolean(input, "capture", "capture");
        ReflectBoolean(input, "webkitdirectory", "webkitdirectory");
        ReflectBoolean(script, "async", "async");
        ReflectBoolean(script, "defer", "defer");
        ReflectBoolean(script, "noModule", "nomodule");
        ReflectString(anchor, "download", "download");
        ReflectEnum(anchor, "referrerPolicy", "referrerpolicy", ["no-referrer", "no-referrer-when-downgrade", "origin", "origin-when-cross-origin",
            "same-origin", "strict-origin", "strict-origin-when-cross-origin", "unsafe-url"]);
        ReflectString(anchor, "ping", "ping");
        var frame = _htmlPrototypes["iframe"];
        ReflectEnum(frame, "loading", "loading", ["eager", "lazy"]);
        ReflectString(frame, "sandbox", "sandbox");
        ReflectString(frame, "allow", "allow");
        ReflectString(frame, "srcdoc", "srcdoc");
        var scriptConstructor = (JsObject)_engine.GetGlobal("HTMLScriptElement").Data!;
        Method(scriptConstructor, "supports", 1, (_, a) => JsValue.From(a.Length > 0 && Arg(a, 0).ToJsString() is "classic" or "module" or "importmap" or "speculationrules"));
        var link = _htmlPrototypes["link"];
        Accessor(link, "relList", e => JsValue.From(RelList(Element(e))));
        ReflectEnum(_htmlElementPrototype, "inputMode", "inputmode", ["none", "text", "decimal", "numeric", "tel", "search", "email", "url"]);
        ReflectEnum(_htmlElementPrototype, "enterKeyHint", "enterkeyhint", ["enter", "done", "go", "next", "previous", "search", "send"]);
        Accessor(_htmlElementPrototype, "translate", e => JsValue.From(InheritedEnum(Element(e), "translate", "yes", "no", true)),
            (e, v) => SetReflectedString(Element(e), "translate", v.ToBoolean() ? "yes" : "no"));
        Accessor(_htmlElementPrototype, "contentEditable", e => JsValue.From(EditableState(Element(e))), (e, v) =>
        {
            var value = v.ToJsString().ToLowerInvariant();
            var element = Element(e);
            if (value == "inherit") SetBooleanAttribute(element, "contenteditable", JsValue.From(false));
            else if (value is "true" or "false" or "plaintext-only") SetReflectedString(element, "contenteditable", value);
            else throw new JsException(Error("SyntaxError", "Invalid contentEditable state"));
        });
        Accessor(_htmlElementPrototype, "isContentEditable", e =>
        {
            for (var element = Element(e); element is not null; element = element.ParentElement)
            {
                var state = EditableState(element);
                if (state != "inherit") return JsValue.From(state != "false");
            }
            return JsValue.From(false);
        });
        Accessor(_htmlElementPrototype, "draggable", e => JsValue.From(Element(e).GetAttribute("draggable")?.ToLowerInvariant() switch
        {
            "true" => true, "false" => false, _ => Element(e).LocalName == "img" || Element(e).LocalName == "a" && Element(e).HasAttribute("href")
        }), (e, v) => SetReflectedString(Element(e), "draggable", v.ToBoolean() ? "true" : "false"));
        Accessor(_htmlElementPrototype, "spellcheck", e => JsValue.From(InheritedEnum(Element(e), "spellcheck", "true", "false", true)),
            (e, v) => SetReflectedString(Element(e), "spellcheck", v.ToBoolean() ? "true" : "false"));
        Method(form, "checkValidity", 0, (e, _) => JsValue.From(ValidateForm(Element(e), report: false)));
        Method(form, "reportValidity", 0, (e, _) => JsValue.From(ValidateForm(Element(e), report: true)));
    }

    private JsObject RegisterHtmlElement(string tag, string name)
    {
        if (name == "HTMLElement")
        {
            _htmlPrototypes.Add(tag, _htmlElementPrototype);
            return _htmlElementPrototype;
        }
        var prototype = new JsObject { Prototype = _htmlElementPrototype };
        _htmlPrototypes.Add(tag, prototype);
        Constructor(name, 0, _ => throw TypeError("Illegal constructor"), prototype);
        return prototype;
    }

    private JsObject GetHtmlElementPrototype(IElement element) =>
        element is IHtmlImageElement ? _imagePrototype : _htmlPrototypes.GetValueOrDefault(element.LocalName, _htmlElementPrototype);

    private JsObject RelList(IElement element) => _relLists.GetValue(element, e =>
    {
        var list = NewObject();
        Accessor(list, "length", _ => JsValue.From((double)RelTokens(e).Length));
        Accessor(list, "value", _ => JsValue.From(e.GetAttribute("rel") ?? ""), (_, v) => SetReflectedString(e, "rel", v.ToJsString()));
        Method(list, "contains", 1, (_, a) => JsValue.From(RelTokens(e).Contains(Arg(a, 0).ToJsString(), StringComparer.OrdinalIgnoreCase)));
        Method(list, "supports", 1, (_, a) => JsValue.From(Arg(a, 0).ToJsString() is "preload" or "modulepreload" or "stylesheet" or "icon" or "manifest"));
        Method(list, "item", 1, (_, a) => NullableString(RelTokens(e).ElementAtOrDefault((int)Arg(a, 0).ToNumber())));
        return list;
    });

    private static string[] RelTokens(IElement element) => (element.GetAttribute("rel") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private JsValue SetPopover(IElement element, bool open)
    {
        if (!element.HasAttribute("popover")) throw TypeError("Element is not a popover");
        SetBooleanAttribute(element, "open", JsValue.From(open));
        return JsValue.Undefined;
    }

    private void ReflectBoolean(JsObject prototype, string property, string attribute) =>
        Accessor(prototype, property, e => JsValue.From(Element(e).HasAttribute(attribute)), (e, v) => SetBooleanAttribute(Element(e), attribute, v));

    private void ReflectString(JsObject prototype, string property, string attribute) =>
        Accessor(prototype, property, e => JsValue.From(Element(e).GetAttribute(attribute) ?? ""), (e, v) => SetReflectedString(Element(e), attribute, v.ToJsString()));

    private void ReflectEnum(JsObject prototype, string property, string attribute, string[] keywords) => Accessor(prototype, property, e =>
    {
        var value = Element(e).GetAttribute(attribute);
        return JsValue.From(keywords.FirstOrDefault(keyword => keyword.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "");
    }, (e, v) => SetReflectedString(Element(e), attribute, v.ToJsString()));

    private void SetReflectedString(IElement element, string attribute, string text)
    {
        CheckDomText(text, "Attribute value");
        element.SetAttribute(attribute, text);
        _page.MarkDirty();
    }

    private static string EditableState(IElement element) => element.GetAttribute("contenteditable")?.ToLowerInvariant() switch
    {
        "" or "true" => "true", "false" => "false", "plaintext-only" => "plaintext-only", _ => "inherit"
    };

    private static bool InheritedEnum(IElement element, string attribute, string yes, string no, bool fallback)
    {
        for (IElement? current = element; current is not null; current = current.ParentElement)
        {
            var value = current.GetAttribute(attribute);
            if (value == "" || yes.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
            if (no.Equals(value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return fallback;
    }

    private void BindValidation(JsObject prototype)
    {
        Accessor(prototype, "willValidate", e => JsValue.From(WillValidate(Element(e))));
        Accessor(prototype, "validationMessage", e => JsValue.From(WillValidate(Element(e)) ? Validation(e).ValidationMessage ?? "" : ""));
        Accessor(prototype, "validity", e => JsValue.From(_validityObjects.GetValue(Element(e), NewValidity)));
        Method(prototype, "setCustomValidity", 1, (e, a) =>
        {
            if (a.Length == 0) throw TypeError("setCustomValidity requires a message");
            var message = a[0].ToJsString();
            CheckDomText(message, "Validation message");
            Validation(e).SetCustomValidity(message);
            return JsValue.Undefined;
        });
        Method(prototype, "checkValidity", 0, (e, _) => JsValue.From(ValidateControl(Element(e), report: false)));
        Method(prototype, "reportValidity", 0, (e, _) => JsValue.From(ValidateControl(Element(e), report: true)));
    }

    private JsObject NewValidity(IElement element)
    {
        if (element is not IValidation control) throw TypeError("Expected a form control");
        var validity = NewObject();
        foreach (var (name, read) in new (string, Func<IValidityState, bool>)[] {
            ("valid", v => v.IsValid), ("valueMissing", v => v.IsValueMissing), ("typeMismatch", v => v.IsTypeMismatch),
            ("patternMismatch", v => v.IsPatternMismatch), ("tooLong", v => v.IsTooLong), ("tooShort", v => v.IsTooShort),
            ("rangeUnderflow", v => v.IsRangeUnderflow), ("rangeOverflow", v => v.IsRangeOverflow),
            ("stepMismatch", v => v.IsStepMismatch), ("badInput", v => v.IsBadInput), ("customError", v => v.IsCustomError) })
            Accessor(validity, name, _ => JsValue.From(read(control.Validity)));
        return validity;
    }

    private static IValidation Validation(JsValue value) => Element(value) as IValidation ?? throw TypeError("Expected a form control");
    private static bool WillValidate(IElement element) => element is IValidation { WillValidate: true } && !HtmlForms.IsDisabled(element) &&
        element.Closest("datalist") is null && (element is not (IHtmlInputElement or IHtmlTextAreaElement) || !element.HasAttribute("readonly"));

    private bool ValidateControl(IElement element, bool report)
    {
        if (!WillValidate(element) || ((IValidation)element).Validity.IsValid) return true;
        if (Dispatch(GetWrapper(element), NewEvent("invalid", false, true)) && report) _page.SetFocusedElement(element);
        return false;
    }

    internal bool ValidateForm(IElement element, bool report)
    {
        if (element is not IHtmlFormElement form) throw TypeError("Expected a form");
        var valid = true;
        foreach (var control in form.Elements.ToArray())
            if (!ValidateControl(control, report && valid)) valid = false;
        return valid;
    }
}
