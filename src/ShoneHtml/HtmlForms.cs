using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace ShoneHtml;

internal sealed record HtmlFormRequest(Uri Address, HttpMethod Method, byte[]? Body, string? EncodedQuery = null);

internal static class HtmlForms
{
    public static IElement? FormOwner(IElement control)
    {
        if (control.GetAttribute("form") is { } id)
        {
            var form = control.Owner?.GetElementById(id);
            return form?.LocalName == "form" ? form : null;
        }
        return control.Closest("form");
    }

    public static bool IsDisabled(IElement element)
    {
        if (element.HasAttribute("disabled"))
            return true;
        if (element.LocalName == "option" && element.ParentElement is { LocalName: "optgroup" } group &&
            group.HasAttribute("disabled"))
            return true;
        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (ancestor.LocalName != "fieldset" || !ancestor.HasAttribute("disabled"))
                continue;
            var legend = ancestor.Children.FirstOrDefault(child => child.LocalName == "legend");
            if (legend is null || !legend.Contains(element))
                return true;
        }
        return false;
    }

    public static bool IsSubmitter(IElement element)
    {
        var type = element.GetAttribute("type")?.ToLowerInvariant();
        return element.LocalName == "button" ? type is not ("button" or "reset") :
            element.LocalName == "input" && type is "submit" or "image";
    }

    public static IElement? DefaultSubmitter(IElement form) => Controls(form).FirstOrDefault(IsSubmitter);

    public static IEnumerable<IElement> RadioGroup(IHtmlInputElement input)
    {
        if (string.IsNullOrEmpty(input.Name) || input.Owner is null)
            yield break;
        var form = FormOwner(input);
        foreach (var item in input.Owner.QuerySelectorAll("input"))
            if (item is IHtmlInputElement radio && radio.Type.Equals("radio", StringComparison.OrdinalIgnoreCase) &&
                input.Name == radio.Name && ReferenceEquals(form, FormOwner(radio)))
                yield return item;
    }

    public static HtmlFormRequest? CreateRequest(HtmlPage page, IElement form, IElement? submitter)
    {
        var action = submitter?.GetAttribute("formaction") ?? form.GetAttribute("action");
        var address = string.IsNullOrEmpty(action) ? page.Address : page.ResolveAddress(action);
        if (!HtmlResources.IsHttpAddress(address))
            return null;
        var method = submitter?.GetAttribute("formmethod") ?? form.GetAttribute("method");
        var enctype = submitter?.GetAttribute("formenctype") ?? form.GetAttribute("enctype");
        if (string.Equals(method, "dialog", StringComparison.OrdinalIgnoreCase))
            return null;
        var post = string.Equals(method, "post", StringComparison.OrdinalIgnoreCase);
        if (post && (string.Equals(enctype, "multipart/form-data", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(enctype, "text/plain", StringComparison.OrdinalIgnoreCase)))
            return null;

        var data = Encode(Entries(form, submitter));
        return post ? new(address!, HttpMethod.Post, Encoding.ASCII.GetBytes(data)) :
            new(new UriBuilder(address!) { Query = data }.Uri, HttpMethod.Get, null, data);
    }

    private static IEnumerable<IElement> Controls(IElement form) =>
        form.Owner!.QuerySelectorAll("input, button, select, textarea")
            .Where(control => ReferenceEquals(FormOwner(control), form));

    private static IEnumerable<KeyValuePair<string, string>> Entries(IElement form, IElement? submitter)
    {
        foreach (var control in Controls(form))
        {
            if (IsDisabled(control) || control.Closest("datalist") is not null)
                continue;
            var name = control.GetAttribute("name") ?? "";
            if (control is IHtmlInputElement image && image.Type.Equals("image", StringComparison.OrdinalIgnoreCase))
            {
                if (control == submitter)
                {
                    var prefix = name.Length == 0 ? "" : name + ".";
                    yield return new(prefix + "x", "0");
                    yield return new(prefix + "y", "0");
                }
                continue;
            }
            if (name.Length == 0)
                continue;
            switch (control)
            {
                case IHtmlInputElement input:
                {
                    var type = input.Type.ToLowerInvariant();
                    if (type is "button" or "reset" || type == "submit" && control != submitter ||
                        type is "checkbox" or "radio" && !input.IsChecked)
                        break;
                    var value = type == "file" ? "" : type is "checkbox" or "radio" && !input.HasAttribute("value") ? "on" :
                        type == "hidden" && name.Equals("_charset_", StringComparison.OrdinalIgnoreCase) ? "UTF-8" : input.Value;
                    yield return new(name, value);
                    break;
                }
                case IHtmlTextAreaElement textArea:
                    yield return new(name, textArea.Value);
                    break;
                case IHtmlSelectElement select:
                    foreach (var option in select.Options)
                        if (option.IsSelected && !IsDisabled(option))
                            yield return new(name, option.Value);
                    break;
                case IHtmlButtonElement when control == submitter:
                    yield return new(name, control.GetAttribute("value") ?? "");
                    break;
            }
        }
    }

    internal static string Encode(IEnumerable<KeyValuePair<string, string>> entries) => string.Join('&', entries.Select(entry =>
        EncodeComponent(NormalizeLines(entry.Key)) + "=" + EncodeComponent(NormalizeLines(entry.Value))));

    private static string NormalizeLines(string text) => text.ReplaceLineEndings("\n")
        .Replace("\n", "\r\n", StringComparison.Ordinal);

    internal static string EncodeComponent(string text)
    {
        const string hex = "0123456789ABCDEF";
        var output = new StringBuilder(text.Length);
        foreach (var octet in Encoding.UTF8.GetBytes(text))
        {
            if (octet is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or
                (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_')
                output.Append((char)octet);
            else if (octet == ' ')
                output.Append('+');
            else
                output.Append('%').Append(hex[octet >> 4]).Append(hex[octet & 15]);
        }
        return output.ToString();
    }
}
