// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Aprillz.MewUI;

namespace ShoneHtml;

internal readonly record struct HtmlTextStyle(string? FontFamily, double FontSize, bool Bold, bool Italic, Color Color)
{
    internal static readonly HtmlTextStyle Default = new(null, 16, false, false, Color.FromRgb(0, 0, 0));
}

internal enum HtmlDisplay { Inline, Block, InlineBlock, None }
internal enum HtmlWhiteSpace { Normal, NoWrap, Pre, PreWrap }
internal enum CssUnit { Pixels, Em, Rem, Percent }

internal readonly record struct CssLength(double Value, CssUnit Unit = CssUnit.Pixels)
{
    internal double Resolve(double width, double font, double rootFont) => Math.Clamp(Unit switch
    {
        CssUnit.Em => Value * font,
        CssUnit.Rem => Value * rootFont,
        CssUnit.Percent => Value * width / 100,
        _ => Value
    }, -1_000_000, 1_000_000);
}

internal readonly record struct CssEdges(CssLength Top, CssLength Right, CssLength Bottom, CssLength Left)
{
    internal CssEdges(CssLength all) : this(all, all, all, all) { }
}

internal record struct HtmlStyle(HtmlTextStyle Text)
{
    internal HtmlDisplay Display;
    internal HtmlWhiteSpace WhiteSpace;
    internal CssEdges Margin;
    internal CssEdges Padding;
    internal CssLength? Width;
    internal CssLength? Height;
    internal CssLength? LineHeight;
    internal bool BorderBox;
    internal Color? Background;
    internal Color? BorderColor;
    internal CssLength BorderWidth = new(3);
    internal bool HasBorder;
}

internal sealed class HtmlStyles
{
    private static readonly Regex Comments = new("/\\*[^*]*\\*+(?:[^/*][^*]*\\*+)*/|\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'", RegexOptions.NonBacktracking | RegexOptions.Singleline);
    private readonly Dictionary<string, List<Rule>> _rules = new(StringComparer.Ordinal);
    private readonly List<Rule> _matching = [];
    private readonly Dictionary<Rule, Dictionary<IElement, int>> _ancestorProgress = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<IElement> _ancestors = new();
    private List<string> _sources = [];

    internal void EndLayout()
    {
        _matching.Clear();
        _ancestorProgress.Clear();
        _ancestors.Clear();
    }

    internal void Update(IDocument document)
    {
        List<string> sources = [];
        for (var node = document.FirstChild; node is not null; node = NextNode(node, document))
        {
            if (node is IElement { LocalName: "style" } element)
                sources.Add(element.TextContent);
        }

        if (sources.SequenceEqual(_sources, StringComparer.Ordinal))
            return;

        _sources = sources;
        _rules.Clear();
        var order = 0;
        foreach (var source in sources)
            ReadSheet(source, ref order);
    }

    private static INode? NextNode(INode node, IDocument root)
    {
        if (node.FirstChild is { } child)
            return child;
        for (INode? cursor = node; cursor is not null && !ReferenceEquals(cursor, root); cursor = cursor.Parent)
        {
            if (cursor.NextSibling is { } sibling)
                return sibling;
        }
        return null;
    }

    internal HtmlStyle Compute(IElement element, in HtmlStyle parent, double rootFont)
    {
        var parentStyle = parent;
        var style = DefaultStyle(element, parentStyle);
        if (_rules.Count > 0)
        {
            _matching.Clear();
            AddMatches("*");
            AddMatches(element.LocalName);
            if (element.Id is { Length: > 0 } id)
                AddMatches("#" + id);
            foreach (var className in element.ClassList)
                AddMatches("." + className);
            _matching.Sort(Rule.Compare);
        }

        var inlineText = element.GetAttribute("style");
        Declaration[] inline = string.IsNullOrEmpty(inlineText) ? [] : ReadDeclarations(StripComments(inlineText).AsSpan());
        ApplyRules(false);
        ApplyRules(true);
        _matching.Clear();
        if (element.LocalName is "head" or "script" or "style" or "template")
            style.Display = HtmlDisplay.None;
        return style;

        void AddMatches(string key)
        {
            if (!_rules.TryGetValue(key, out var rules))
                return;
            foreach (var rule in rules)
            {
                if (Matches(rule, element))
                    _matching.Add(rule);
            }
        }

        void ApplyRules(bool important)
        {
            foreach (var rule in _matching)
                foreach (var declaration in rule.Declarations)
                    if (declaration.Important == important)
                        Apply(ref style, declaration, parentStyle, rootFont);
            foreach (var declaration in inline)
                if (declaration.Important == important)
                    Apply(ref style, declaration, parentStyle, rootFont);
        }
    }

    private bool Matches(Rule rule, IElement element)
    {
        var parts = rule.Parts;
        if (!parts[^1].Matches(element))
            return false;
        var target = parts.Length - 1;
        if (target == 0)
            return true;
        if (!_ancestorProgress.TryGetValue(rule, out var progressByElement))
            _ancestorProgress.Add(rule, progressByElement = new(ReferenceEqualityComparer.Instance));

        // Remember how many of the left-hand compounds matched along each ancestor path.
        // This also makes a failing descendant rule linear on a deeply nested document.
        var matched = 0;
        for (var ancestor = element.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (progressByElement.TryGetValue(ancestor, out matched))
                break;
            _ancestors.Push(ancestor);
        }
        while (_ancestors.TryPop(out var ancestor))
        {
            if (matched < target && parts[matched].Matches(ancestor))
                matched++;
            progressByElement.Add(ancestor, matched);
        }
        return matched == target;
    }

    private static HtmlStyle DefaultStyle(IElement element, HtmlStyle parent)
    {
        var style = new HtmlStyle(parent.Text) { WhiteSpace = parent.WhiteSpace, LineHeight = parent.LineHeight };
        switch (element.LocalName)
        {
            case "html": case "main": case "section": case "article": case "aside": case "nav": case "header":
            case "footer": case "div": case "form": case "ul": case "ol": case "li": case "dl": case "dt":
            case "dd": case "figure": case "figcaption": case "address": case "details": case "summary":
            case "table": case "thead": case "tbody": case "tfoot": case "tr": case "caption":
                style.Display = HtmlDisplay.Block;
                break;
            case "body":
                style.Display = HtmlDisplay.Block;
                style.Margin = new(new CssLength(8));
                break;
            case "p": case "blockquote": case "pre":
                style.Display = HtmlDisplay.Block;
                var vertical = new CssLength(1, CssUnit.Em);
                var horizontal = element.LocalName == "blockquote" ? new CssLength(40) : default;
                style.Margin = new(vertical, horizontal, vertical, horizontal);
                if (element.LocalName == "pre")
                {
                    style.Text = style.Text with { FontFamily = "monospace" };
                    style.WhiteSpace = HtmlWhiteSpace.Pre;
                }
                break;
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                style.Display = HtmlDisplay.Block;
                var (scale, margin) = element.LocalName switch
                {
                    "h1" => (2, .67), "h2" => (1.5, .83), "h3" => (1.17, 1),
                    "h4" => (1, 1.33), "h5" => (.83, 1.67), _ => (.67, 2.33)
                };
                style.Text = style.Text with { Bold = true, FontSize = Math.Clamp(parent.Text.FontSize * scale, 1, 512) };
                var headingMargin = new CssLength(margin, CssUnit.Em);
                style.Margin = new(headingMargin, default, headingMargin, default);
                break;
            case "b": case "strong":
                style.Text = style.Text with { Bold = true };
                break;
            case "i": case "em": case "cite": case "var":
                style.Text = style.Text with { Italic = true };
                break;
            case "small":
                style.Text = style.Text with { FontSize = Math.Max(1, parent.Text.FontSize * .8) };
                break;
            case "code": case "kbd": case "samp":
                style.Text = style.Text with { FontFamily = "monospace" };
                break;
            case "a" when element.HasAttribute("href"):
                style.Text = style.Text with { Color = Color.FromRgb(0, 0, 238) };
                break;
            case "button":
                style.Display = HtmlDisplay.InlineBlock;
                style.Padding = new(new CssLength(3), new CssLength(8), new CssLength(3), new CssLength(8));
                style.Background = Color.FromRgb(239, 239, 239);
                style.BorderColor = Color.FromRgb(118, 118, 118);
                style.BorderWidth = new(1);
                style.HasBorder = true;
                break;
            case "hr":
                style.Display = HtmlDisplay.Block;
                style.Height = new(1);
                style.Background = Color.FromRgb(128, 128, 128);
                var halfEm = new CssLength(.5, CssUnit.Em);
                style.Margin = new(halfEm, default, halfEm, default);
                break;
        }
        if (element.HasAttribute("hidden"))
            style.Display = HtmlDisplay.None;
        return style;
    }

    private void ReadSheet(string input, ref int order)
    {
        var css = StripComments(input).AsSpan();
        while (!(css = css.TrimStart()).IsEmpty)
        {
            var isAtRule = css[0] == '@';
            var opening = FindDelimiter(css, isAtRule ? "{;" : "{");
            if (opening < 0)
                break;
            if (css[opening] == ';')
            {
                css = css[(opening + 1)..];
                continue;
            }
            var selectors = css[..opening];
            css = css[(opening + 1)..];
            var closing = EndBlock(css);
            if (closing < 0)
                break;
            if (!isAtRule)
            {
                var declarations = ReadDeclarations(css[..closing]);
                if (declarations.Length > 0)
                {
                    foreach (var text in selectors.ToString().Split(','))
                    {
                        if (!Rule.TryParse(text.AsSpan(), declarations, order, out var rule))
                            continue;
                        if (!_rules.TryGetValue(rule.Key, out var list))
                            _rules.Add(rule.Key, list = []);
                        list.Add(rule);
                    }
                    order++;
                }
            }
            css = css[(closing + 1)..];
        }
    }

    private static string StripComments(string css) => Comments.Replace(css, static match => match.Value[0] == '/' ? "" : match.Value);

    private static int EndBlock(ReadOnlySpan<char> css)
    {
        var level = 1;
        var offset = 0;
        while (offset < css.Length)
        {
            var delimiter = FindDelimiter(css[offset..], "{}");
            if (delimiter < 0)
                return -1;
            offset += delimiter;
            if (css[offset] == '{')
                level++;
            else if (--level == 0)
                return offset;
            offset++;
        }
        return -1;
    }

    private static int FindDelimiter(ReadOnlySpan<char> css, string delimiters)
    {
        var parentheses = 0;
        var quote = '\0';
        for (var i = 0; i < css.Length; i++)
        {
            var character = css[i];
            if (character == '\\')
            {
                i++;
                continue;
            }
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                continue;
            }
            if (character is '\'' or '"')
                quote = character;
            else if (character is '(' or '[')
                parentheses++;
            else if (character is ')' or ']')
                parentheses = Math.Max(0, parentheses - 1);
            else if (parentheses == 0 && delimiters.Contains(character))
                return i;
        }
        return -1;
    }

    private static Declaration[] ReadDeclarations(ReadOnlySpan<char> css)
    {
        List<Declaration> declarations = [];
        while (!css.IsEmpty)
        {
            var end = FindDelimiter(css, ";");
            var declaration = end < 0 ? css : css[..end];
            css = end < 0 ? [] : css[(end + 1)..];
            var colon = declaration.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = declaration[..colon].Trim().ToString().ToLowerInvariant();
            var value = declaration[(colon + 1)..].Trim();
            var bang = FindDelimiter(value, "!");
            var important = bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase);
            if (important)
                value = value[..bang].TrimEnd();
            if (!value.IsEmpty)
                declarations.Add(new(name, value.ToString(), important));
        }
        return [.. declarations];
    }

    private static void Apply(ref HtmlStyle style, Declaration declaration, in HtmlStyle parent, double rootFont)
    {
        var value = declaration.Value;
        var keyword = value.ToLowerInvariant();
        var inherit = keyword == "inherit";
        var initial = keyword == "initial";
        switch (declaration.Name)
        {
            case "display":
                style.Display = keyword switch
                {
                    "none" => HtmlDisplay.None, "block" or "list-item" or "flow-root" => HtmlDisplay.Block,
                    "inline" or "initial" => HtmlDisplay.Inline, "inline-block" => HtmlDisplay.InlineBlock,
                    "inherit" => parent.Display, _ => style.Display
                };
                break;
            case "white-space":
                style.WhiteSpace = keyword switch
                {
                    "normal" or "initial" => HtmlWhiteSpace.Normal, "nowrap" => HtmlWhiteSpace.NoWrap,
                    "pre" => HtmlWhiteSpace.Pre, "pre-wrap" or "break-spaces" => HtmlWhiteSpace.PreWrap,
                    "inherit" or "unset" => parent.WhiteSpace, _ => style.WhiteSpace
                };
                break;
            case "color":
                if (TryColor(value, out var color) || inherit || initial || keyword is "currentcolor" or "unset")
                    style.Text = style.Text with { Color = initial ? HtmlTextStyle.Default.Color : inherit || keyword is "currentcolor" or "unset" ? parent.Text.Color : color };
                break;
            case "font-family":
                if (inherit || initial || keyword == "unset")
                    style.Text = style.Text with { FontFamily = initial ? null : parent.Text.FontFamily };
                else
                {
                    var separator = FindDelimiter(value, ",");
                    var family = (separator < 0 ? value : value[..separator]).Trim().Trim('"', '\'');
                    if (family.Length > 0)
                        style.Text = style.Text with { FontFamily = family };
                }
                break;
            case "font-size":
                if (inherit || initial || keyword == "unset")
                    style.Text = style.Text with { FontSize = initial ? HtmlTextStyle.Default.FontSize : parent.Text.FontSize };
                else if (TryLength(value, false, out var size))
                    style.Text = style.Text with { FontSize = Math.Clamp(size.Resolve(parent.Text.FontSize, parent.Text.FontSize, rootFont), 1, 512) };
                else if (keyword is "small" or "medium" or "large" or "x-large" or "xx-large" or "smaller" or "larger")
                    style.Text = style.Text with { FontSize = Math.Clamp(keyword switch
                    {
                        "small" => 13, "medium" => 16, "large" => 18, "x-large" => 24, "xx-large" => 32,
                        "smaller" => parent.Text.FontSize * .8, _ => parent.Text.FontSize * 1.2
                    }, 1, 512) };
                break;
            case "font-weight":
                if (inherit || keyword == "unset")
                    style.Text = style.Text with { Bold = parent.Text.Bold };
                else if (keyword is "bold" or "bolder" or "normal" or "lighter" or "initial")
                    style.Text = style.Text with { Bold = keyword is "bold" or "bolder" };
                else if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var weight) && weight is >= 1 and <= 1000)
                    style.Text = style.Text with { Bold = weight >= 600 };
                break;
            case "font-style":
                if (inherit || keyword == "unset")
                    style.Text = style.Text with { Italic = parent.Text.Italic };
                else if (keyword is "italic" or "oblique" or "normal" or "initial")
                    style.Text = style.Text with { Italic = keyword is "italic" or "oblique" };
                break;
            case "line-height":
                if (inherit || keyword == "unset") style.LineHeight = parent.LineHeight;
                else if (initial || keyword == "normal") style.LineHeight = null;
                else if (TryLength(value, false, out var lineHeight))
                    style.LineHeight = lineHeight.Unit == CssUnit.Percent ? new(lineHeight.Value / 100, CssUnit.Em) : lineHeight;
                else if (TryNumber(value, out var multiplier) && multiplier >= 0) style.LineHeight = new(multiplier, CssUnit.Em);
                break;
            case "background": case "background-color":
                if (inherit) style.Background = parent.Background;
                else if (keyword is "none" or "initial" or "unset") style.Background = null;
                else if (TryColor(value, out var background)) style.Background = background;
                break;
            case "border":
                if (TryBorder(value, out var borderWidth, out var visible, out var borderColor))
                {
                    style.BorderWidth = borderWidth;
                    style.HasBorder = visible;
                    style.BorderColor = borderColor;
                }
                break;
            case "border-width":
                if (TryBorderWidth(value, out var width)) style.BorderWidth = width;
                break;
            case "border-style":
                if (TryBorderStyle(keyword, out var hasBorder)) style.HasBorder = hasBorder;
                break;
            case "border-color":
                if (keyword == "currentcolor") style.BorderColor = null;
                else if (TryColor(value, out var border)) style.BorderColor = border;
                break;
            case "box-sizing":
                if (keyword is "border-box" or "content-box" or "initial") style.BorderBox = keyword == "border-box";
                break;
            case "width":
                if (keyword is "auto" or "initial" or "unset") style.Width = null;
                else if (inherit) style.Width = parent.Width;
                else if (TryLength(value, false, out var widthValue)) style.Width = widthValue;
                break;
            case "height":
                if (keyword is "auto" or "initial" or "unset") style.Height = null;
                else if (inherit) style.Height = parent.Height;
                else if (TryLength(value, false, out var heightValue)) style.Height = heightValue;
                break;
            case "margin": case "padding":
                var isMargin = declaration.Name == "margin";
                if (TryEdges(value, isMargin, out var edges))
                {
                    if (isMargin) style.Margin = edges;
                    else style.Padding = edges;
                }
                break;
            case "margin-top": case "margin-right": case "margin-bottom": case "margin-left":
                if (TryEdge(value, true, out var margin))
                    style.Margin = SetEdge(style.Margin, declaration.Name.AsSpan(7), margin);
                break;
            case "padding-top": case "padding-right": case "padding-bottom": case "padding-left":
                if (TryEdge(value, false, out var padding))
                    style.Padding = SetEdge(style.Padding, declaration.Name.AsSpan(8), padding);
                break;
        }
    }

    private static CssEdges SetEdge(CssEdges edges, ReadOnlySpan<char> side, CssLength length) => side switch
    {
        "top" => edges with { Top = length }, "right" => edges with { Right = length },
        "bottom" => edges with { Bottom = length }, _ => edges with { Left = length }
    };

    private static bool TryEdges(string value, bool negative, out CssEdges edges)
    {
        edges = default;
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
            return false;
        Span<CssLength> lengths = stackalloc CssLength[4];
        for (var i = 0; i < parts.Length; i++)
            if (!TryEdge(parts[i], negative, out lengths[i]))
                return false;
        edges = new(lengths[0], parts.Length > 1 ? lengths[1] : lengths[0],
            parts.Length > 2 ? lengths[2] : lengths[0], parts.Length > 3 ? lengths[3] : parts.Length > 1 ? lengths[1] : lengths[0]);
        return true;
    }

    private static bool TryEdge(ReadOnlySpan<char> value, bool negative, out CssLength length)
    {
        length = default;
        return value.Equals("initial", StringComparison.OrdinalIgnoreCase)
               || (negative && value.Equals("auto", StringComparison.OrdinalIgnoreCase))
               || TryLength(value, negative, out length);
    }

    private static bool TryLength(ReadOnlySpan<char> value, bool negative, out CssLength length)
    {
        length = default;
        if (value.IsEmpty)
            return false;
        var unit = CssUnit.Pixels;
        var scale = 1d;
        var number = value;
        if (value.EndsWith("rem", StringComparison.OrdinalIgnoreCase)) { unit = CssUnit.Rem; number = value[..^3]; }
        else if (value.EndsWith("em", StringComparison.OrdinalIgnoreCase)) { unit = CssUnit.Em; number = value[..^2]; }
        else if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) number = value[..^2];
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) { number = value[..^2]; scale = 4d / 3; }
        else if (value[^1] == '%') { unit = CssUnit.Percent; number = value[..^1]; }
        if (!TryNumber(number, out var magnitude) || (!negative && magnitude < 0) || (number.Length == value.Length && magnitude != 0))
            return false;
        length = new(Math.Clamp(magnitude, -1_000_000, 1_000_000) * scale, unit);
        return true;
    }

    private static bool TryNumber(ReadOnlySpan<char> value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    private static bool TryBorderWidth(ReadOnlySpan<char> value, out CssLength width)
    {
        if (value.Equals("thin", StringComparison.OrdinalIgnoreCase)) { width = new(1); return true; }
        if (value.Equals("medium", StringComparison.OrdinalIgnoreCase)) { width = new(3); return true; }
        if (value.Equals("thick", StringComparison.OrdinalIgnoreCase)) { width = new(5); return true; }
        return TryLength(value, false, out width) && width.Unit != CssUnit.Percent;
    }

    private static bool TryBorderStyle(string value, out bool visible)
    {
        visible = value is "solid" or "dotted" or "dashed" or "double" or "groove" or "ridge" or "inset" or "outset";
        return visible || value is "none" or "hidden";
    }

    private static bool TryBorder(string value, out CssLength width, out bool visible, out Color? color)
    {
        width = new(3);
        visible = false;
        color = null;
        var hasWidth = false;
        var hasStyle = false;
        var hasColor = false;
        var remaining = value.AsSpan().Trim();
        while (!remaining.IsEmpty)
        {
            var end = FindDelimiter(remaining, " \t\r\n\f");
            var token = end < 0 ? remaining : remaining[..end];
            remaining = end < 0 ? [] : remaining[(end + 1)..].TrimStart();
            if (!hasWidth && TryBorderWidth(token, out var parsedWidth)) { width = parsedWidth; hasWidth = true; }
            else if (!hasStyle && TryBorderStyle(token.ToString().ToLowerInvariant(), out var parsedStyle)) { visible = parsedStyle; hasStyle = true; }
            else if (!hasColor && token.Equals("currentcolor", StringComparison.OrdinalIgnoreCase)) hasColor = true;
            else if (!hasColor && TryColor(token, out var parsedColor)) { color = parsedColor; hasColor = true; }
            else return false;
        }
        return hasStyle || hasWidth || hasColor;
    }

    private static bool TryColor(ReadOnlySpan<char> value, out Color color)
    {
        color = default;
        if (value.IsEmpty)
            return false;
        if (value[0] == '#')
        {
            var hex = value[1..];
            if (hex.Length is not (3 or 4 or 6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var packed))
                return false;
            if (hex.Length is 3 or 4)
            {
                if (hex.Length == 3) packed = (packed << 4) | 15;
                color = Color.FromArgb((byte)((packed & 15) * 17), (byte)(((packed >> 12) & 15) * 17),
                    (byte)(((packed >> 8) & 15) * 17), (byte)(((packed >> 4) & 15) * 17));
            }
            else
            {
                if (hex.Length == 6) packed = (packed << 8) | 255;
                color = Color.FromArgb((byte)packed, (byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8));
            }
            return true;
        }
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase))
            return true;
        if (value[^1] == ')' && (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) || value.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = value[(value.IndexOf('(') + 1)..^1].ToString().Split([',', '/', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is not (3 or 4)) return false;
            Span<byte> channels = [0, 0, 0, 255];
            for (var i = 0; i < parts.Length; i++)
            {
                var text = parts[i].AsSpan();
                var percent = text[^1] == '%';
                if (!TryNumber(percent ? text[..^1] : text, out var amount)) return false;
                if (percent) amount = amount / 100 * 255;
                else if (i == 3) amount *= 255;
                channels[i] = (byte)Math.Round(Math.Clamp(amount, 0, 255));
            }
            color = Color.FromArgb(channels[3], channels[0], channels[1], channels[2]);
            return true;
        }
        if (value.Equals("rebeccapurple", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.FromRgb(102, 51, 153);
            return true;
        }
        var named = System.Drawing.Color.FromName(value.ToString());
        if (!named.IsKnownColor || named.IsSystemColor)
            return false;
        color = Color.FromArgb(named.A, named.R, named.G, named.B);
        return true;
    }

    private readonly record struct Declaration(string Name, string Value, bool Important);

    private sealed record Rule(Compound[] Parts, int IdCount, int ClassCount, int TagCount, int Order, Declaration[] Declarations)
    {
        internal string Key => Parts[^1].Key;

        internal static int Compare(Rule x, Rule y)
        {
            var compare = x.IdCount.CompareTo(y.IdCount);
            if (compare == 0) compare = x.ClassCount.CompareTo(y.ClassCount);
            if (compare == 0) compare = x.TagCount.CompareTo(y.TagCount);
            return compare == 0 ? x.Order.CompareTo(y.Order) : compare;
        }

        internal static bool TryParse(ReadOnlySpan<char> selector, Declaration[] declarations, int order, out Rule rule)
        {
            rule = null!;
            List<Compound> parts = [];
            var ids = 0;
            var classes = 0;
            var tags = 0;
            while (!(selector = selector.TrimStart()).IsEmpty)
            {
                var offset = selector.IndexOfAny(" \t\r\n\f");
                var current = offset < 0 ? selector : selector[..offset];
                selector = offset < 0 ? [] : selector[offset..];
                if (!Compound.TryParse(current, out var compound))
                    return false;
                parts.Add(compound);
                ids += compound.Ids.Length;
                classes += compound.Classes.Length;
                if (compound.Tag is not null) tags++;
            }
            if (parts.Count == 0)
                return false;
            rule = new([.. parts], ids, classes, tags, order, declarations);
            return true;
        }
    }

    private sealed record Compound(string? Tag, string[] Ids, string[] Classes)
    {
        internal string Key => Ids.Length > 0 ? "#" + Ids[0] : Classes.Length > 0 ? "." + Classes[0] : Tag ?? "*";

        internal bool Matches(IElement element)
        {
            if (Tag is not null && !Tag.Equals(element.LocalName, StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (var id in Ids)
                if (!id.Equals(element.Id, StringComparison.Ordinal))
                    return false;
            foreach (var className in Classes)
                if (!element.ClassList.Contains(className))
                    return false;
            return true;
        }

        internal static bool TryParse(ReadOnlySpan<char> text, out Compound compound)
        {
            compound = null!;
            List<string>? ids = null;
            List<string>? classes = null;
            string? tag = null;
            if (text[0] == '*') text = text[1..];
            else if (text[0] is not ('#' or '.'))
            {
                tag = ReadName(ref text)?.ToLowerInvariant();
                if (tag is null) return false;
            }
            while (!text.IsEmpty)
            {
                var prefix = text[0];
                if (prefix is not ('#' or '.')) return false;
                text = text[1..];
                var name = ReadName(ref text);
                if (name is null) return false;
                if (prefix == '#') (ids ??= []).Add(name);
                else (classes ??= []).Add(name);
            }
            compound = new(tag, ids?.ToArray() ?? [], classes?.ToArray() ?? []);
            return true;
        }

        private static string? ReadName(ref ReadOnlySpan<char> text)
        {
            var length = 0;
            while (length < text.Length && (char.IsAsciiLetterOrDigit(text[length]) || text[length] is '_' or '-' || text[length] >= 128))
                length++;
            if (length == 0) return null;
            var name = text[..length].ToString();
            text = text[length..];
            return name;
        }
    }
}
