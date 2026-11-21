using System.Globalization;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Aprillz.MewUI;

namespace ShoneHtml;

internal sealed record HtmlRendererSnapshot(string Title, HtmlRendererNode[] Nodes);
internal sealed record HtmlRendererNode(int Id, int Parent, string? Tag, string? Text,
    Dictionary<string, string>? Attributes, string? Css, HtmlRendererPixels? Pixels);
internal sealed record HtmlRendererPixels(int Width, int Height, byte[] Rgba);
internal sealed record HtmlRendererMirror(IDocument Document, Dictionary<IElement, int> Ids,
    Dictionary<IElement, string> Styles, Dictionary<IElement, HtmlRendererPixels> Images);

internal static class HtmlRendererProjection
{
    private const int MaximumNodes = 20_000;
    private const int MaximumDepth = 96;
    private const int MaximumTitle = 4_096;
    private const int MaximumText = 256 * 1024;
    private const int MaximumCharacters = 1024 * 1024;
    private const int MaximumAttributes = 32;
    private const int MaximumAttributeLength = 8_192;
    private const int MaximumControlValue = 32_768;
    private const int MaximumSheetLength = 64 * 1024;
    private const int MaximumCssCharacters = 128 * 1024;
    private const int MaximumCssDelimiters = 4_096;
    private const int MaximumImageDimension = 4_096;
    private const int MaximumImageBytes = 10 * 1024 * 1024;

    // Wire-only state is not installed as attributes on the mirror.
    private const string LinkMarker = "$link";
    private const string LiveValue = "$value";
    private const string Checked = "$checked";
    private const string Indeterminate = "$indeterminate";
    private const string Selected = "$selected";
    private const string Selection = "$selection";

    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "html", "head", "body", "style", "link",
        "main", "section", "article", "aside", "nav", "header", "footer", "div", "span",
        "p", "blockquote", "pre", "h1", "h2", "h3", "h4", "h5", "h6", "hgroup", "hr", "br", "wbr",
        "b", "strong", "i", "em", "cite", "var", "small", "code", "kbd", "samp", "u", "s", "del", "ins",
        "sup", "sub", "mark", "q", "abbr", "dfn", "bdi", "bdo", "ruby", "rp", "rt", "time", "data",
        "ul", "ol", "li", "dl", "dt", "dd", "figure", "figcaption", "address", "details", "summary",
        "table", "thead", "tbody", "tfoot", "tr", "caption", "col", "colgroup", "td", "th",
        "a", "img", "canvas", "picture", "form", "fieldset", "legend", "label", "button", "input", "textarea",
        "select", "option", "optgroup", "datalist", "output", "progress", "meter"
    };

    private static readonly HashSet<string> Leaves = new(StringComparer.Ordinal)
    {
        "br", "col", "hr", "img", "input", "link", "style", "wbr"
    };

    internal static HtmlRendererSnapshot Capture(HtmlPage page, Func<IElement, int> getId)
    {
        var title = page.Title;
        var budget = new Budget();
        budget.Text(title, MaximumTitle);
        var nodes = new List<HtmlRendererNode>();
        var ids = new HashSet<int>();
        var visited = 0;
        if (page.Document.DocumentElement is { LocalName: "html" } root)
            Visit(root, 0, 1);
        return new(title, nodes.ToArray());

        void Visit(INode node, int parent, int depth)
        {
            Require(++visited <= MaximumNodes && depth <= MaximumDepth);
            if (node is IText text)
            {
                budget.Text(text.Data, MaximumText);
                nodes.Add(new(0, parent, null, text.Data, null, null, null));
                return;
            }
            if (node is not IElement element || element.NamespaceUri != NamespaceNames.HtmlUri ||
                !Tags.Contains(element.LocalName) || parent != 0 && element.LocalName == "html")
                return;

            var tag = element.LocalName;
            var id = getId(element);
            Require(id > 0 && ids.Add(id));
            var attributes = CaptureAttributes(element);
            budget.Attributes(tag, attributes);
            var css = tag switch
            {
                "style" => element.TextContent,
                "link" => page.GetExternalStyle(element),
                _ => null
            };
            if (css is not null)
                budget.Css(css, MaximumSheetLength);
            HtmlRendererPixels? pixels = null;
            if (tag == "img" && page.GetImage(element) is { } encoded)
                pixels = Decode(encoded, budget);
            else if (tag == "canvas" && page.GetCanvasPixels(element) is { } canvas)
            {
                budget.Image(PixelBytes(canvas));
                pixels = canvas with { Rgba = (byte[])canvas.Rgba.Clone() };
            }
            nodes.Add(new(id, parent, tag, null, attributes, css, pixels));
            if (!Leaves.Contains(tag))
                for (var child = node.FirstChild; child is not null; child = child.NextSibling)
                    Visit(child, id, depth + 1);
        }
    }

    internal static HtmlRendererMirror Materialize(HtmlRendererSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Title is null || snapshot.Nodes is not { Length: <= MaximumNodes })
            throw InvalidProjection();
        var budget = new Budget();
        budget.Text(snapshot.Title, MaximumTitle);
        var positions = new Dictionary<int, (string Tag, int Depth)>();
        var hasRoot = false;

        // Validate structure and budgets before constructing the host DOM.
        foreach (var node in snapshot.Nodes)
        {
            if (node is null)
                throw InvalidProjection();
            var depth = 1;
            if (node.Parent != 0)
            {
                if (!positions.TryGetValue(node.Parent, out var parent) || Leaves.Contains(parent.Tag))
                    throw InvalidProjection();
                depth = parent.Depth + 1;
            }
            Require(depth <= MaximumDepth);
            if (node.Tag is not { } tag)
            {
                if (node.Id != 0 || node.Parent == 0 || node.Text is null || node.Attributes is not null ||
                    node.Css is not null || node.Pixels is not null)
                    throw InvalidProjection();
                budget.Text(node.Text, MaximumText);
                continue;
            }

            Require(node.Text is null && Tags.Contains(tag) && node.Id > 0 && positions.TryAdd(node.Id, (tag, depth)));
            if (node.Parent == 0)
            {
                Require(tag == "html" && !hasRoot);
                hasRoot = true;
            }
            else
                Require(tag != "html");
            budget.Attributes(tag, node.Attributes);
            if (node.Css is { } css)
            {
                Require(tag is "style" or "link");
                budget.Css(css, MaximumSheetLength);
            }
            if (node.Pixels is { } pixels)
            {
                Require(tag is "img" or "canvas");
                if (tag == "canvas")
                    Require(pixels.Width == HtmlCanvas.ParseDimension(node.Attributes?.GetValueOrDefault("width"), 300) &&
                        pixels.Height == HtmlCanvas.ParseDimension(node.Attributes?.GetValueOrDefault("height"), 150));
                budget.Image(PixelBytes(pixels));
            }
        }

        // The constant creates an empty document with a context that has no loader or script engine.
        var document = new HtmlParser().ParseDocument("");
        document.RemoveChild(document.DocumentElement!);
        var ids = new Dictionary<IElement, int>(ReferenceEqualityComparer.Instance);
        var styles = new Dictionary<IElement, string>(ReferenceEqualityComparer.Instance);
        var images = new Dictionary<IElement, HtmlRendererPixels>(ReferenceEqualityComparer.Instance);
        var elements = new Dictionary<int, IElement>();
        foreach (var node in snapshot.Nodes)
        {
            INode parent = node.Parent == 0 ? document : elements[node.Parent];
            if (node.Tag is not { } tag)
            {
                parent.AppendChild(document.CreateTextNode(node.Text!));
                continue;
            }

            var element = document.CreateElement(tag);
            if (node.Attributes is { } attributes)
                foreach (var (name, value) in attributes)
                {
                    if (name == LinkMarker)
                        element.SetAttribute("href", "#"); // Styling/focus only; navigation is a typed worker action.
                    else if (name[0] != '$')
                        element.SetAttribute(name, value);
                }
            parent.AppendChild(element);
            elements.Add(node.Id, element);
            ids.Add(element, node.Id);
            if (node.Css is { } css)
            {
                styles.Add(element, css);
                if (tag == "style")
                    element.AppendChild(document.CreateTextNode(css));
            }
            if (node.Pixels is { } pixels)
                images.Add(element, pixels);
        }

        // Apply live values after the full tree exists (textarea defaults and select options depend on children).
        foreach (var node in snapshot.Nodes)
            if (node.Attributes is { } attributes)
                RestoreFormState(elements[node.Id], attributes);
        foreach (var node in snapshot.Nodes)
            if (node.Attributes?.GetValueOrDefault(Selection) is { } selection && elements[node.Id] is IHtmlSelectElement select)
            {
                var index = int.Parse(selection, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                var options = select.Options.ToArray();
                Require(index >= -1 && index < options.Length && !select.IsMultiple);
                for (var option = 0; option < options.Length; option++)
                    options[option].IsSelected = option == index;
            }

        if (document.DocumentElement is { } html)
        {
            var head = html.Children.FirstOrDefault(element => element.LocalName == "head");
            if (head is null)
            {
                head = document.CreateElement("head");
                html.InsertBefore(head, html.FirstChild);
            }
            var title = document.CreateElement("title");
            title.AppendChild(document.CreateTextNode(snapshot.Title));
            head.AppendChild(title);
        }
        else if (snapshot.Title.Length != 0)
            throw InvalidProjection();
        return new(document, ids, styles, images);
    }

    internal static ImageSource CreateImageSource(HtmlRendererPixels pixels)
    {
        _ = PixelBytes(pixels);
        var bgra = (byte[])pixels.Rgba.Clone();
        SwapRedBlue(bgra);
        return ImageSource.FromBgraPixels(pixels.Width, pixels.Height, bgra, true);
    }

    private static HtmlRendererPixels? Decode(byte[] bytes, Budget budget)
    {
        var count = HtmlRaster.GetPixelCount(bytes);
        if (count == 0)
            return null;
        budget.CheckImage(checked((int)count * 4));
        var pixels = HtmlImageDecoder.Decode(bytes);
        if (pixels is not null)
            budget.Image(PixelBytes(pixels));
        return pixels;
    }

    private static void SwapRedBlue(byte[] pixels)
    {
        for (var index = 0; index < pixels.Length; index += 4)
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
    }

    private static int PixelBytes(HtmlRendererPixels pixels)
    {
        if (pixels.Width is <= 0 or > MaximumImageDimension || pixels.Height is <= 0 or > MaximumImageDimension ||
            pixels.Rgba is null || pixels.Rgba.Length != (long)pixels.Width * pixels.Height * 4 ||
            pixels.Rgba.Length > MaximumImageBytes)
            throw InvalidProjection();
        return pixels.Rgba.Length;
    }

    private static Dictionary<string, string>? CaptureAttributes(IElement element)
    {
        Dictionary<string, string>? attributes = null;
        foreach (var attribute in element.Attributes)
            if (attribute.NamespaceUri is null or "" && AllowedAttribute(element.LocalName, attribute.LocalName))
                Add(attribute.LocalName, attribute.Value);
        if (element.LocalName == "a" && element.HasAttribute("href"))
            Add(LinkMarker, "");
        switch (element)
        {
            case IHtmlInputElement input:
                if (input.Type != "file")
                    Add(LiveValue, input.Value);
                if (input.Type is "checkbox" or "radio")
                    Add(Checked, Boolean(input.IsChecked));
                if (input.Type == "checkbox")
                    Add(Indeterminate, Boolean(input.IsIndeterminate));
                break;
            case IHtmlTextAreaElement area:
                Add(LiveValue, area.Value);
                break;
            case IHtmlOptionElement option:
                Add(Selected, Boolean(option.IsSelected));
                break;
            case IHtmlSelectElement { IsMultiple: false } select:
                Add(Selection, select.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                break;
        }
        return attributes;

        void Add(string name, string value)
        {
            attributes ??= new(StringComparer.Ordinal);
            Require(attributes.Count < MaximumAttributes);
            attributes.Add(name, value);
        }

        static string Boolean(bool value) => value ? "1" : "0";
    }

    private static void RestoreFormState(IElement element, Dictionary<string, string> attributes)
    {
        switch (element)
        {
            case IHtmlInputElement input:
                if (input.Type != "file" && attributes.GetValueOrDefault(LiveValue) is { } value)
                    input.Value = value;
                if (input.Type is "checkbox" or "radio" && attributes.GetValueOrDefault(Checked) is { } check)
                    input.IsChecked = check == "1";
                if (input.Type == "checkbox" && attributes.GetValueOrDefault(Indeterminate) is { } indeterminate)
                    input.IsIndeterminate = indeterminate == "1";
                break;
            case IHtmlTextAreaElement area when attributes.GetValueOrDefault(LiveValue) is { } areaValue:
                area.Value = areaValue;
                break;
            case IHtmlOptionElement option when attributes.GetValueOrDefault(Selected) is { } selected:
                option.IsSelected = selected == "1";
                break;
        }
    }

    private static bool AllowedAttribute(string tag, string name) => name switch
    {
        "id" or "class" or "style" or "title" or "lang" or "dir" or "hidden" or "tabindex" or "role" => true,
        "name" => tag is "a" or "button" or "form" or "input" or "select" or "textarea" or "output" or "fieldset",
        "alt" => tag == "img",
        "width" or "height" => tag is "img" or "canvas",
        "disabled" => tag is "button" or "fieldset" or "input" or "optgroup" or "option" or "select" or "textarea" or "link",
        "type" => tag is "input" or "button" or "ol",
        "value" => tag is "input" or "button" or "option" or "li" or "data" or "progress" or "meter",
        "max" => tag is "input" or "progress" or "meter",
        "min" or "step" => tag is "input" or "meter",
        "checked" => tag == "input",
        "readonly" or "placeholder" or "maxlength" or "minlength" => tag is "input" or "textarea",
        "required" => tag is "input" or "select" or "textarea",
        "multiple" => tag is "input" or "select",
        "size" => tag is "input" or "select",
        "rows" or "cols" or "wrap" => tag == "textarea",
        "selected" => tag == "option",
        "label" => tag is "option" or "optgroup",
        "for" => tag is "label" or "output",
        "form" => tag is "button" or "fieldset" or "input" or "select" or "textarea" or "output",
        "open" => tag == "details",
        "media" => tag is "style" or "link",
        "rel" => tag == "link",
        "colspan" or "rowspan" or "scope" => tag is "td" or "th",
        "span" => tag is "col" or "colgroup",
        "reversed" or "start" => tag == "ol",
        "low" or "high" or "optimum" => tag == "meter",
        "datetime" => tag is "time" or "del" or "ins",
        _ => IsDataAttribute(name)
    };

    private static bool IsDataAttribute(string name)
    {
        if (name.Length is <= 5 or > 64 ||
            !(name.StartsWith("data-", StringComparison.Ordinal) || name.StartsWith("aria-", StringComparison.Ordinal)))
            return false;
        foreach (var c in name.AsSpan(5))
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
                return false;
        return true;
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw InvalidProjection();
    }

    private static InvalidDataException InvalidProjection() => new("Invalid or oversized renderer projection.");

    private sealed class Budget
    {
        private int _characters;
        private int _cssCharacters;
        private int _cssDelimiters;
        private int _imageBytes;

        internal void Text(string value, int limit)
        {
            Require(value.Length <= limit && value.Length <= MaximumCharacters - _characters);
            _characters += value.Length;
        }

        internal void Css(string css, int limit)
        {
            Text(css, limit);
            Require(css.Length <= MaximumCssCharacters - _cssCharacters);
            _cssCharacters += css.Length;
            // ShoneHtml CSS is a render-only grammar: no loader, @import, URL images or font faces.
            foreach (var c in css)
                if (c is '{' or ';' or ',' && ++_cssDelimiters > MaximumCssDelimiters)
                    throw InvalidProjection();
        }

        internal void Attributes(string tag, Dictionary<string, string>? attributes)
        {
            if (attributes is null)
                return;
            Require(attributes.Count <= MaximumAttributes);
            foreach (var (name, value) in attributes)
            {
                if (value is null)
                    throw InvalidProjection();
                Require(name switch
                {
                    LinkMarker => tag == "a" && value.Length == 0,
                    LiveValue => tag is "input" or "textarea",
                    Checked or Indeterminate => tag == "input" && value is "0" or "1",
                    Selected => tag == "option" && value is "0" or "1",
                    Selection => tag == "select" && value.Length <= 6 &&
                        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var index) &&
                        index is >= -1 and < MaximumNodes,
                    _ => AllowedAttribute(tag, name)
                });
                Text(name, 64);
                if (name == "style")
                    Css(value, MaximumAttributeLength);
                else
                    Text(value, name == LiveValue ? MaximumControlValue : MaximumAttributeLength);
            }
        }

        internal void CheckImage(int length) => Require(length <= MaximumImageBytes - _imageBytes);

        internal void Image(int length)
        {
            CheckImage(length);
            _imageBytes += length;
        }
    }
}
