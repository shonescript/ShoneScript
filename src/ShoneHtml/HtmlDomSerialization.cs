using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private void BindDomSerialization()
    {
        var parserPrototype = NewObject();
        Method(parserPrototype, "parseFromString", 2, (_, a) =>
        {
            if (a.Length < 2 || a[1].ToJsString() != "text/html")
                throw TypeError("Unsupported DOMParser MIME type");
            var markup = a[0].ToJsString();
            if (Renderer is not null) CheckDomText(markup, "Markup");
            var document = new HtmlParser().ParseDocument(markup);
            AccountNode(document);
            return Wrap(document);
        });
        Constructor("DOMParser", 0, _ => JsValue.From(new JsObject { Prototype = parserPrototype }), parserPrototype);

        var serializerPrototype = NewObject();
        Method(serializerPrototype, "serializeToString", 1, (_, a) =>
        {
            var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            Node(Arg(a, 0)).ToHtml(writer, AngleSharp.Html.HtmlMarkupFormatter.Instance);
            return JsValue.From(writer.ToString());
        });
        Constructor("XMLSerializer", 0, _ => JsValue.From(new JsObject { Prototype = serializerPrototype }), serializerPrototype);
    }
}
