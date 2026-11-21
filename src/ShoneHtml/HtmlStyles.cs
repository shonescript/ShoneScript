// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Aprillz.MewUI;

namespace ShoneHtml;

internal readonly record struct HtmlTextStyle(string? FontFamily, double FontSize, bool Bold, bool Italic, Color Color)
{
    // ShoneHtml uses a dark canvas unless a document supplies its own CSS colors.
    internal static readonly HtmlTextStyle Default = new(null, 16, false, false, Color.FromRgb(232, 232, 232));
}

internal enum HtmlDisplay { Inline, Block, InlineBlock, None, Flex, InlineFlex, Grid, InlineGrid }
internal enum HtmlPosition { Static, Relative, Absolute, Fixed }
internal enum HtmlOverflow { Visible, Hidden, Clip, Auto, Scroll }
internal enum HtmlAlign { Normal, Start, Center, End, Stretch, SpaceBetween, SpaceAround, SpaceEvenly }
internal readonly record struct CssTrack(CssLength? Length, double Fraction = 0, CssLength? Minimum = null);
internal enum HtmlWhiteSpace { Normal, NoWrap, Pre, PreWrap }
internal enum CssUnit { Pixels, Em, Rem, Percent, Auto }

internal readonly record struct CssLength(double Value, CssUnit Unit = CssUnit.Pixels)
{
    internal double Resolve(double width, double font, double rootFont) => Math.Clamp(Unit switch
    {
        CssUnit.Em => Value * font,
        CssUnit.Rem => Value * rootFont,
        CssUnit.Percent => Value * width / 100,
        CssUnit.Auto => 0,
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
    internal CssLength? MinWidth;
    internal CssLength? MaxWidth;
    internal CssLength? MinHeight;
    internal CssLength? MaxHeight;
    internal CssLength? Top, Right, Bottom, Left;
    internal HtmlPosition Position;
    internal HtmlOverflow OverflowX, OverflowY;
    internal int? ZIndex;
    internal CssLength RowGap, ColumnGap;
    internal CssLength? FlexBasis;
    internal double FlexGrow;
    internal double FlexShrink = 1;
    internal bool FlexColumn, FlexReverse, FlexWrap, FlexWrapReverse;
    internal int Order;
    internal HtmlAlign JustifyContent, AlignItems, AlignSelf;
    internal CssTrack[]? GridColumns, GridRows;
    internal int GridColumn, GridRow, GridColumnSpan = 1, GridRowSpan = 1;
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
    private readonly Dictionary<Rule, Dictionary<(IElement Element, int Part), bool>> _matches = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<(IElement Element, int Part, bool Expanded)> _selectorStack = new();
    private List<(string Text, string Media)> _sources = [];
    private double _width;
    private double _height;
    private IElement? _focused;
    private IElement? _hovered;
    private IElement? _active;
    private bool _dark;

    internal void BeginLayout(double width, double height, IElement? focused, IElement? hovered, IElement? active, bool dark = true)
    {
        _width = width;
        _height = height;
        _focused = focused;
        _hovered = hovered;
        _active = active;
        _dark = dark;
    }

    internal void EndLayout()
    {
        _matching.Clear();
        _matches.Clear();
        _selectorStack.Clear();
        _mediaMatches.Clear();
        _focused = _hovered = _active = null;
    }

    internal void Update(IDocument document, Func<IElement, string?>? externalStyleSheet)
    {
        List<(string Text, string Media)> sources = [];
        for (var node = document.FirstChild; node is not null; node = NextNode(node, document))
        {
            if (node is not IElement element) continue;
            var text = element.LocalName switch
            {
                "style" => element.TextContent,
                "link" when IsStyleSheet(element) => externalStyleSheet?.Invoke(element),
                _ => null
            };
            if (text is not null) sources.Add((text, element.GetAttribute("media") ?? ""));
        }

        if (sources.SequenceEqual(_sources))
            return;

        _sources = sources;
        _rules.Clear();
        var order = 0;
        foreach (var source in sources)
            ReadSheet(source.Text, ref order, string.IsNullOrWhiteSpace(source.Media) ? [] : [source.Media]);
    }

    private static bool IsStyleSheet(IElement element)
    {
        var rel = element.GetAttribute("rel")?.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries) ?? [];
        return rel.Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && !element.HasAttribute("disabled") &&
            !rel.Contains("alternate", StringComparer.OrdinalIgnoreCase);
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
        var style = DefaultStyle(element, parentStyle, _dark);
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
        var (overflowX, overflowY) = (style.OverflowX, style.OverflowY);
        style.OverflowX = ComputeOverflow(overflowX, overflowY);
        style.OverflowY = ComputeOverflow(overflowY, overflowX);
        if (element.LocalName is "head" or "script" or "style" or "template" or "link" ||
            element is IHtmlInputElement { Type: "hidden" }) style.Display = HtmlDisplay.None;
        return style;

        void AddMatches(string key)
        {
            if (!_rules.TryGetValue(key, out var rules))
                return;
            foreach (var rule in rules)
            {
                if (rule.Media.All(MatchesMedia) && Matches(rule, element))
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
        var target = rule.Parts.Length - 1;
        if (!rule.Parts[target].Matches(element, this)) return false;
        if (target == 0) return true;
        var relation = rule.Combinators[target - 1];
        var first = Previous(element, relation);
        if (first is null) return false;
        if (!_matches.TryGetValue(rule, out var cached)) _matches.Add(rule, cached = new());

        // Cache prefix searches (including sibling/ancestor scans) so failed chains do not
        // backtrack exponentially and deeply nested documents do not recurse on the CLR stack.
        var start = (first, target - 1);
        _selectorStack.Push((first, target - 1, false));
        while (_selectorStack.TryPop(out var frame))
        {
            var (candidate, part, expanded) = frame;
            var key = (candidate, part);
            if (cached.ContainsKey(key)) continue;
            var lookup = rule.Combinators[part];
            var next = lookup is ' ' or '~' ? Previous(candidate, lookup) : null;
            var matches = rule.Parts[part].Matches(candidate, this);
            if (matches && part == 0)
            {
                cached[key] = true;
                continue;
            }
            var left = matches ? Previous(candidate, rule.Combinators[part - 1]) : null;
            if (expanded)
                cached[key] = (left is not null && cached[(left, part - 1)]) || (next is not null && cached[(next, part)]);
            else
            {
                _selectorStack.Push((candidate, part, true));
                if (next is not null) _selectorStack.Push((next, part, false));
                if (left is not null) _selectorStack.Push((left, part - 1, false));
            }
        }
        return cached[start];
    }

    private static IElement? Previous(IElement element, char relation) => relation is ' ' or '>'
        ? element.ParentElement : element.PreviousElementSibling;

    private static readonly char[] HtmlSpace = [' ', '\t', '\r', '\n', '\f'];

    private bool MatchesPseudo(IElement element, string pseudo) => pseudo switch
    {
        "checked" => element is IHtmlInputElement input && IsCheckable(input) && input.IsChecked ||
            element is IHtmlOptionElement { IsSelected: true },
        "disabled" => IsControl(element) && IsDisabled(element),
        "enabled" => IsControl(element) && !IsDisabled(element),
        "required" => CanRequire(element) && element.HasAttribute("required"),
        "optional" => CanRequire(element) && !element.HasAttribute("required"),
        "focus" or "focus-visible" => ReferenceEquals(_focused, element),
        "focus-within" => Contains(element, _focused),
        "popover-open" => element.HasAttribute("popover") && element.HasAttribute("open"),
        "hover" => Contains(element, _hovered),
        "active" => Contains(element, _active),
        "link" or "any-link" => element.LocalName is "a" or "area" && element.HasAttribute("href"),
        "root" => element.Parent is IDocument,
        "first-child" => element.PreviousElementSibling is null,
        "last-child" => element.NextElementSibling is null,
        "only-child" => element.PreviousElementSibling is null && element.NextElementSibling is null,
        _ => false
    };

    private static bool IsControl(IElement element) => element.LocalName is "button" or "fieldset" or "input" or "optgroup" or "option" or "select" or "textarea";
    private static bool IsCheckable(IHtmlInputElement input) => input.Type is "checkbox" or "radio";
    private static bool CanRequire(IElement element) => element.LocalName is "select" or "textarea" ||
        element is IHtmlInputElement input && input.Type is not ("hidden" or "range" or "color" or "button" or "submit" or "reset" or "image");

    private static bool IsDisabled(IElement element)
    {
        if (element.HasAttribute("disabled")) return true;
        if (element.LocalName == "option") return element.ParentElement is { LocalName: "optgroup" } group && group.HasAttribute("disabled");
        if (element.LocalName == "optgroup") return false;
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            if (parent.LocalName != "fieldset" || !parent.HasAttribute("disabled")) continue;
            var legend = parent.Children.FirstOrDefault(static child => child.LocalName == "legend");
            if (legend is null || !Contains(legend, element)) return true;
        }
        return false;
    }

    private static bool Contains(IElement element, IElement? descendant)
    {
        for (var cursor = descendant; cursor is not null; cursor = cursor.ParentElement)
            if (ReferenceEquals(cursor, element)) return true;
        return false;
    }

    private static HtmlStyle DefaultStyle(IElement element, HtmlStyle parent, bool dark)
    {
        var style = new HtmlStyle(parent.Text) { WhiteSpace = parent.WhiteSpace, LineHeight = parent.LineHeight };
        if (element.LocalName is "html" or "body")
        {
            style.Background = dark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(255, 255, 255);
            style.Text = style.Text with { Color = dark ? Color.FromRgb(232, 232, 232) : Color.FromRgb(0, 0, 0) };
        }
        switch (element.LocalName)
        {
            case "html": case "main": case "section": case "article": case "aside": case "nav": case "header":
            case "footer": case "div": case "form": case "fieldset": case "ul": case "ol": case "li": case "dl": case "dt":
            case "dd": case "figure": case "figcaption": case "address": case "details": case "summary":
            case "table": case "thead": case "tbody": case "tfoot": case "tr": case "caption":
                style.Display = HtmlDisplay.Block;
                if (element.LocalName == "html") style.Background = dark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(255, 255, 255);
                break;
            case "body":
                style.Display = HtmlDisplay.Block;
                style.Margin = new(new CssLength(8));
                style.Background = dark ? Color.FromRgb(30, 30, 30) : Color.FromRgb(255, 255, 255);
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
                style.Text = style.Text with { Color = Color.FromRgb(104, 179, 255) };
                break;
            case "input" when string.Equals(element.GetAttribute("type"), "hidden", StringComparison.OrdinalIgnoreCase):
                style.Display = HtmlDisplay.None;
                break;
            case "img": case "input": case "select": case "textarea":
                style.Display = HtmlDisplay.InlineBlock;
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

    private void ReadSheet(string input, ref int order, string[] media) => ReadRules(StripComments(input), ref order, media, 0);

    private void ReadRules(ReadOnlySpan<char> css, ref int order, string[] media, int depth)
    {
        if (depth > 32) return;
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
                    while (!selectors.IsEmpty)
                    {
                        var comma = FindDelimiter(selectors, ",");
                        var text = comma < 0 ? selectors : selectors[..comma];
                        selectors = comma < 0 ? [] : selectors[(comma + 1)..];
                        if (!Rule.TryParse(text, declarations, media, order, out var rule)) continue;
                        if (!_rules.TryGetValue(rule.Key, out var list)) _rules.Add(rule.Key, list = []);
                        list.Add(rule);
                    }
                    order++;
                }
            }
            else if (selectors.StartsWith("@media", StringComparison.OrdinalIgnoreCase) && selectors.Length > 6 &&
                     (char.IsWhiteSpace(selectors[6]) || selectors[6] == '('))
                ReadRules(css[..closing], ref order, [.. media, selectors[6..].Trim().ToString()], depth + 1);
            css = css[(closing + 1)..];
        }
    }

    private readonly Dictionary<string, bool> _mediaMatches = new(StringComparer.Ordinal);

    private bool MatchesMedia(string media)
    {
        if (_mediaMatches.TryGetValue(media, out var match)) return match;
        foreach (var query in media.ToLowerInvariant().Split(','))
            if (MatchesMediaQuery(query.AsSpan().Trim())) return _mediaMatches[media] = true;
        return _mediaMatches[media] = false;
    }

    private bool MatchesMediaQuery(ReadOnlySpan<char> query)
    {
        var negate = ReadWord(ref query, "not");
        if (!negate) ReadWord(ref query, "only");
        var result = true;
        if (query.IsEmpty) return false;
        if (query[0] != '(')
        {
            var space = query.IndexOfAny(" \t\r\n\f");
            var type = space < 0 ? query : query[..space];
            result = type is "screen" or "all";
            if (space < 0) return result != negate;
            query = query[space..].TrimStart();
            if (!ReadWord(ref query, "and")) return false;
        }
        while (!query.IsEmpty)
        {
            if (query[0] != '(') return false;
            var end = query.IndexOf(')');
            if (end < 0) return false;
            result &= MatchesMediaFeature(query[1..end].Trim());
            query = query[(end + 1)..].TrimStart();
            if (query.IsEmpty) return result != negate;
            if (!ReadWord(ref query, "and") || query.IsEmpty) return false;
        }
        return false;
    }

    private bool MatchesMediaFeature(ReadOnlySpan<char> feature)
    {
        var separator = feature.IndexOf(':');
        if (separator < 0) return feature is "width" ? _width > 0 : feature is "height" && _height > 0;
        var name = feature[..separator].TrimEnd();
        var value = feature[(separator + 1)..].Trim();
        if (name is "orientation")
            return value is "portrait" ? _height >= _width : value is "landscape" && _width > _height;
        if (name is "hover" or "any-hover") return value is "hover";
        if (name is "pointer" or "any-pointer") return value is "fine";
        if (name is "prefers-color-scheme") return value is "light";
        if (name is "prefers-reduced-motion") return value is "no-preference";
        if (!TryLength(value, false, out var length) || length.Unit == CssUnit.Percent) return false;
        var boundary = length.Resolve(0, HtmlTextStyle.Default.FontSize, HtmlTextStyle.Default.FontSize);
        return name switch
        {
            "min-width" => _width >= boundary, "max-width" => _width <= boundary, "width" => _width == boundary,
            "min-height" => _height >= boundary, "max-height" => _height <= boundary, "height" => _height == boundary,
            _ => false
        };
    }

    private static bool ReadWord(ref ReadOnlySpan<char> text, string word)
    {
        if (!text.StartsWith(word, StringComparison.Ordinal) || (text.Length > word.Length && !char.IsWhiteSpace(text[word.Length])))
            return false;
        text = text[word.Length..].TrimStart();
        return true;
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

    // Deliberately omit fallback mappings (e.g. flow-root to block) and properties ignored by layout.
    internal static bool SupportsDeclaration(string property, string value)
    {
        if (property.Length > 128 || value.Length > 4096) return false;
        var name = property.Trim().ToLowerInvariant();
        var keyword = StripComments(value).Trim().ToLowerInvariant();
        if (keyword.Length == 0) return false;
        return name switch
        {
            "display" => ParseDisplay(keyword) is not null,
            "box-sizing" => keyword is "border-box" or "content-box",
            "position" => keyword is "static" or "relative" or "absolute" or "fixed",
            "overflow" => TryOverflow(keyword, out _, out _),
            "overflow-x" or "overflow-y" => ParseOverflow(keyword) is not null || keyword is "inherit" or "initial" or "unset",
            "inset" => TryInsets(keyword, out _),
            "top" or "right" or "bottom" or "left" => TryOffset(keyword, out _),
            "width" or "height" => keyword == "auto" || TryLength(keyword, false, out _),
            "color" or "border-color" => keyword == "currentcolor" || TryColor(keyword, out _),
            "background-color" => TryColor(keyword, out _),
            "gap" or "row-gap" or "column-gap" => ValidGap(name, keyword.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries), out _),
            "grid-template-columns" or "grid-template-rows" => TryTracks(keyword, out _),
            _ => false
        };
    }

    internal static bool SupportsCondition(string condition)
    {
        if (condition.Length > 4096) return false;
        var text = StripComments(condition).AsSpan().Trim();
        if (text.StartsWith("selector(", StringComparison.OrdinalIgnoreCase) && text[^1] == ')')
        {
            var selector = text[9..^1];
            if (selector.Contains('\\') || !Rule.TryParse(selector, [], [], 0, out var rule)) return false;
            return rule.Parts.All(part => ValidName(part.Tag) && part.Ids.All(ValidName) && part.Classes.All(ValidName) &&
                part.Attributes.All(attribute => ValidName(attribute.Name) && attribute.Flag != 's') &&
                part.Pseudos.All(pseudo => pseudo is "checked" or "disabled" or "enabled" or "required" or "optional" or
                    "focus" or "focus-within" or "popover-open" or "hover" or "active" or "link" or "any-link" or "root" or
                    "first-child" or "last-child" or "only-child"));
        }
        if (text.Length < 3 || text[0] != '(' || text[^1] != ')') return false;
        var body = text[1..^1];
        var colon = body.IndexOf(':');
        return colon > 0 && SupportsDeclaration(body[..colon].ToString(), body[(colon + 1)..].ToString());

        static bool ValidName(string? name) => name is null || (!char.IsAsciiDigit(name[0]) &&
            (name[0] != '-' || name.Length > 1 && !char.IsAsciiDigit(name[1])));
    }

    private static HtmlDisplay? ParseDisplay(string value) => value switch
    {
        "none" => HtmlDisplay.None, "block" => HtmlDisplay.Block, "inline" => HtmlDisplay.Inline,
        "inline-block" => HtmlDisplay.InlineBlock, "flex" => HtmlDisplay.Flex, "inline-flex" => HtmlDisplay.InlineFlex,
        "grid" => HtmlDisplay.Grid, "inline-grid" => HtmlDisplay.InlineGrid, _ => null
    };

    private static HtmlOverflow? ParseOverflow(string keyword) => keyword switch
    {
        "visible" => HtmlOverflow.Visible, "hidden" => HtmlOverflow.Hidden, "clip" => HtmlOverflow.Clip,
        "auto" or "overlay" => HtmlOverflow.Auto, "scroll" => HtmlOverflow.Scroll, _ => null
    };

    private static bool TryOverflow(string keyword, out HtmlOverflow x, out HtmlOverflow y)
    {
        x = y = HtmlOverflow.Visible;
        if (keyword is "inherit" or "initial" or "unset") return true;
        var parts = keyword.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || ParseOverflow(parts[0]) is not { } first ||
            ParseOverflow(parts[^1]) is not { } last) return false;
        (x, y) = (first, last);
        return true;
    }

    private static HtmlOverflow ComputeOverflow(HtmlOverflow axis, HtmlOverflow other) =>
        other is HtmlOverflow.Visible or HtmlOverflow.Clip ? axis : axis switch
        {
            HtmlOverflow.Visible => HtmlOverflow.Auto,
            HtmlOverflow.Clip => HtmlOverflow.Hidden,
            _ => axis
        };

    private static bool ValidGap(string name, string[] parts, out CssLength first)
    {
        first = default;
        return parts.Length is 1 or 2 && TryGap(parts[0], out first) &&
            (parts.Length == 1 || name == "gap" && TryGap(parts[1], out _));
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
                style.Display = ParseDisplay(keyword) ?? (keyword switch
                {
                    "list-item" or "flow-root" => HtmlDisplay.Block, "initial" => HtmlDisplay.Inline,
                    "inherit" => parent.Display, _ => style.Display
                });
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
            case "min-width": case "max-width": case "min-height": case "max-height":
                if (keyword is "none" or "auto" or "initial" or "unset" || TryLength(value, false, out _))
                {
                    CssLength? limit = TryLength(value, false, out var parsed) ? parsed : null;
                    switch (declaration.Name)
                    {
                        case "min-width": style.MinWidth = limit; break;
                        case "max-width": style.MaxWidth = limit; break;
                        case "min-height": style.MinHeight = limit; break;
                        case "max-height": style.MaxHeight = limit; break;
                    }
                }
                break;
            case "overflow":
                if (TryOverflow(keyword, out var overflowX, out var overflowY))
                    (style.OverflowX, style.OverflowY) = inherit ? (parent.OverflowX, parent.OverflowY) : (overflowX, overflowY);
                break;
            case "overflow-x":
                style.OverflowX = inherit ? parent.OverflowX : initial || keyword == "unset" ? HtmlOverflow.Visible :
                    ParseOverflow(keyword) ?? style.OverflowX;
                break;
            case "overflow-y":
                style.OverflowY = inherit ? parent.OverflowY : initial || keyword == "unset" ? HtmlOverflow.Visible :
                    ParseOverflow(keyword) ?? style.OverflowY;
                break;
            case "position":
                style.Position = keyword switch
                {
                    "static" or "initial" or "unset" => HtmlPosition.Static, "relative" => HtmlPosition.Relative,
                    "absolute" => HtmlPosition.Absolute, "fixed" => HtmlPosition.Fixed, "inherit" => parent.Position,
                    _ => style.Position
                };
                break;
            case "z-index":
                if (keyword is "auto" or "initial" or "unset") style.ZIndex = null;
                else if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var zIndex)) style.ZIndex = zIndex;
                break;
            case "inset":
                if (TryInsets(keyword, out var offsets))
                    (style.Top, style.Right, style.Bottom, style.Left) = offsets;
                break;
            case "top": case "right": case "bottom": case "left":
                if (TryOffset(keyword, out var offset))
                    switch (declaration.Name)
                    {
                        case "top": style.Top = offset; break;
                        case "right": style.Right = offset; break;
                        case "bottom": style.Bottom = offset; break;
                        case "left": style.Left = offset; break;
                    }
                break;
            case "gap": case "row-gap": case "column-gap":
                var gaps = keyword.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries);
                if (ValidGap(declaration.Name, gaps, out var firstGap))
                {
                    if (declaration.Name != "column-gap") style.RowGap = firstGap;
                    if (declaration.Name != "row-gap") style.ColumnGap = gaps.Length == 2 && TryGap(gaps[1], out var secondGap) ? secondGap : firstGap;
                }
                break;
            case "flex-flow":
                foreach (var token in keyword.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries))
                    if (!ApplyDirection(ref style, token)) ApplyWrap(ref style, token);
                break;
            case "flex-direction": ApplyDirection(ref style, keyword); break;
            case "flex-wrap": ApplyWrap(ref style, keyword); break;
            case "flex-grow":
                if (TryNumber(value, out var grow) && grow >= 0) style.FlexGrow = Math.Min(grow, 1_000_000);
                break;
            case "flex-shrink":
                if (TryNumber(value, out var shrink) && shrink >= 0) style.FlexShrink = Math.Min(shrink, 1_000_000);
                break;
            case "flex-basis":
                if (TryBasis(keyword, out var basis)) style.FlexBasis = basis;
                break;
            case "flex": ApplyFlex(ref style, keyword); break;
            case "order":
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemOrder)) style.Order = itemOrder;
                break;
            case "justify-content":
                if (TryAlign(keyword, out var justify)) style.JustifyContent = justify;
                break;
            case "align-items":
                if (TryAlign(keyword, out var alignItems)) style.AlignItems = alignItems;
                break;
            case "align-self":
                if (TryAlign(keyword, out var alignSelf)) style.AlignSelf = alignSelf;
                break;
            case "grid-template-columns": case "grid-template-rows":
                if (TryTracks(keyword, out var tracks))
                {
                    if (declaration.Name == "grid-template-columns") style.GridColumns = tracks;
                    else style.GridRows = tracks;
                }
                break;
            case "grid-column":
                if (TryPlacement(keyword, out var column, out var columnSpan)) (style.GridColumn, style.GridColumnSpan) = (column, columnSpan);
                break;
            case "grid-row":
                if (TryPlacement(keyword, out var row, out var rowSpan)) (style.GridRow, style.GridRowSpan) = (row, rowSpan);
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

    private static bool TryGap(string value, out CssLength length) => TryLength(value, false, out length) || value == "normal";

    private static bool TryOffset(string value, out CssLength? offset)
    {
        offset = null;
        if (value is "auto" or "initial" or "unset") return true;
        if (!TryLength(value, true, out var length)) return false;
        offset = length;
        return true;
    }

    private static bool TryInsets(string value, out (CssLength?, CssLength?, CssLength?, CssLength?) offsets)
    {
        offsets = default;
        var parts = value.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4) return false;
        Span<CssLength?> values = stackalloc CssLength?[4];
        for (var i = 0; i < parts.Length; i++)
            if (!TryOffset(parts[i], out values[i])) return false;
        offsets = (values[0], parts.Length > 1 ? values[1] : values[0], parts.Length > 2 ? values[2] : values[0],
            parts.Length > 3 ? values[3] : parts.Length > 1 ? values[1] : values[0]);
        return true;
    }

    private static bool ApplyDirection(ref HtmlStyle style, string value)
    {
        if (value is not ("row" or "row-reverse" or "column" or "column-reverse")) return false;
        style.FlexColumn = value.StartsWith("column", StringComparison.Ordinal);
        style.FlexReverse = value.EndsWith("-reverse", StringComparison.Ordinal);
        return true;
    }

    private static bool ApplyWrap(ref HtmlStyle style, string value)
    {
        if (value is not ("nowrap" or "wrap" or "wrap-reverse")) return false;
        style.FlexWrap = value != "nowrap";
        style.FlexWrapReverse = value == "wrap-reverse";
        return true;
    }

    private static bool TryBasis(string value, out CssLength? basis)
    {
        basis = null;
        if (value is "auto" or "content") return true;
        if (!TryLength(value, false, out var length)) return false;
        basis = length;
        return true;
    }

    private static void ApplyFlex(ref HtmlStyle style, string value)
    {
        if (value is "none" or "auto" or "initial")
        {
            (style.FlexGrow, style.FlexShrink, style.FlexBasis) = (value == "auto" ? 1 : 0, value == "none" ? 0 : 1, null);
            return;
        }
        var parts = value.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3) return;
        double grow = 1, shrink = 1;
        CssLength? basis = new(0, CssUnit.Percent);
        var i = 0;
        if (TryNumber(parts[0], out var first) && first >= 0)
        {
            grow = Math.Min(first, 1_000_000);
            i++;
            if (i < parts.Length && TryNumber(parts[i], out var second) && second >= 0)
            {
                shrink = Math.Min(second, 1_000_000);
                i++;
            }
        }
        if (i < parts.Length && (i != parts.Length - 1 || !TryBasis(parts[i], out basis))) return;
        (style.FlexGrow, style.FlexShrink, style.FlexBasis) = (grow, shrink, basis);
    }

    private static bool TryAlign(string value, out HtmlAlign align)
    {
        align = value switch
        {
            "center" => HtmlAlign.Center, "start" or "flex-start" or "left" => HtmlAlign.Start,
            "end" or "flex-end" or "right" => HtmlAlign.End, "stretch" => HtmlAlign.Stretch,
            "space-between" => HtmlAlign.SpaceBetween, "space-around" => HtmlAlign.SpaceAround,
            "space-evenly" => HtmlAlign.SpaceEvenly, _ => HtmlAlign.Normal
        };
        return align != HtmlAlign.Normal || value is "normal" or "auto" or "initial";
    }

    private static bool TryTracks(ReadOnlySpan<char> value, out CssTrack[]? tracks, bool allowRepeat = true)
    {
        tracks = null;
        if (value is "none" or "initial") return true;
        List<CssTrack> list = [];
        while (!(value = value.TrimStart()).IsEmpty)
        {
            var end = FindDelimiter(value, " \t\r\n\f");
            var token = end < 0 ? value : value[..end];
            value = end < 0 ? [] : value[(end + 1)..];
            if (allowRepeat && token.StartsWith("repeat(", StringComparison.Ordinal) && token.EndsWith(")"))
            {
                var args = token[7..^1];
                var comma = FindDelimiter(args, ",");
                if (comma < 0 || !int.TryParse(args[..comma].Trim(), out var count) || count is < 1 or > 128 ||
                    !TryTracks(args[(comma + 1)..], out var repeated, false) || repeated is null ||
                    repeated.Length * count + list.Count > 128) return false;
                for (var i = 0; i < count; i++) list.AddRange(repeated);
            }
            else if (TryTrack(token, out var track)) list.Add(track);
            else return false;
            if (list.Count > 128) return false;
        }
        if (list.Count == 0) return false;
        tracks = [.. list];
        return true;
    }

    private static bool TryTrack(ReadOnlySpan<char> token, out CssTrack track)
    {
        track = default;
        if (token is "auto" or "min-content" or "max-content") return true;
        if (TryLength(token, false, out var length)) { track = new(length); return true; }
        if (token.EndsWith("fr") && TryNumber(token[..^2], out var fraction) && fraction >= 0)
        {
            track = new(null, Math.Min(fraction, 1_000_000));
            return true;
        }
        if (!token.StartsWith("minmax(", StringComparison.Ordinal) || !token.EndsWith(")")) return false;
        var args = token[7..^1];
        var comma = args.IndexOf(',');
        if (comma < 0) return false;
        var min = args[..comma].Trim();
        var max = args[(comma + 1)..].Trim();
        CssLength? minimum = TryLength(min, false, out var parsedMin) ? parsedMin : null;
        if (minimum is null && min is not ("auto" or "min-content" or "max-content")) return false;
        if (TryLength(max, false, out var maximum)) { track = new(maximum, 0, minimum); return true; }
        if (max.EndsWith("fr") && TryNumber(max[..^2], out fraction) && fraction >= 0)
        {
            track = new(null, Math.Min(fraction, 1_000_000), minimum);
            return true;
        }
        if (max is not ("auto" or "min-content" or "max-content")) return false;
        track = new(null, 0, minimum);
        return true;
    }

    private static bool TryPlacement(string value, out int start, out int span)
    {
        start = 0;
        span = 1;
        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts[0].StartsWith("span ", StringComparison.Ordinal))
            return parts.Length == 1 && int.TryParse(parts[0].AsSpan(5), out span) && span is >= 1 and <= 128;
        if (parts[0] != "auto" && (!int.TryParse(parts[0], out start) || start is < 1 or > 128)) return false;
        if (parts.Length == 1 || parts[1] == "auto") return true;
        if (parts[1].StartsWith("span ", StringComparison.Ordinal))
            return int.TryParse(parts[1].AsSpan(5), out span) && span is >= 1 and <= 128;
        if (start == 0 || !int.TryParse(parts[1], out var end) || end <= start || end > 129) return false;
        span = end - start;
        return true;
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
        if (negative && value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            length = new(0, CssUnit.Auto);
            return true;
        }
        length = default;
        return value.Equals("initial", StringComparison.OrdinalIgnoreCase) || TryLength(value, negative, out length);
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

    internal static bool TryColor(ReadOnlySpan<char> value, out Color color)
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

    private static int EndAttribute(ReadOnlySpan<char> text)
    {
        var quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '\\') { index++; continue; }
            if (quote == '\0')
            {
                if (ch == ']') return index;
                if (ch is '\'' or '"') quote = ch;
            }
            else if (ch == quote) quote = '\0';
        }
        return -1;
    }

    private readonly record struct AttributeTest(string Name, string Operator, string Value, char Flag)
    {
        internal static bool TryParse(ReadOnlySpan<char> text, out AttributeTest test)
        {
            test = default;
            var name = Compound.ReadName(ref text);
            if (name is null) return false;
            text = text.TrimStart();
            if (text.IsEmpty) { test = new(name, "", "", '\0'); return true; }
            var offset = text[0] == '=' ? 1 : text.Length > 1 && text[1] == '=' && text[0] is '~' or '|' or '^' or '$' or '*' ? 2 : 0;
            if (offset == 0) return false;
            var op = text[..offset].ToString();
            text = text[offset..].TrimStart();
            if (text.IsEmpty) return false;
            string? value;
            if (text[0] is '\'' or '"')
            {
                var quote = text[0];
                var end = 1;
                while (end < text.Length && text[end] != quote)
                    end += text[end] == '\\' ? 2 : 1;
                if (end >= text.Length) return false;
                value = text[1..end].ToString();
                text = text[(end + 1)..];
            }
            else value = Compound.ReadName(ref text);
            if (value is null) return false;
            text = text.Trim();
            var flag = text.IsEmpty ? '\0' : text.Length == 1 ? char.ToLowerInvariant(text[0]) : '?';
            if (flag is not ('\0' or 's' or 'i')) return false;
            test = new(name, op, value, flag);
            return true;
        }

        internal bool Matches(IElement element)
        {
            if (element.GetAttribute(Name) is not { } actual) return false;
            var comparison = Flag == 'i' || (Flag == '\0' && Name.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                element.LocalName is "input" or "button") ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return Operator switch
            {
                "" => true, "=" => actual.Equals(Value, comparison),
                "^=" => Value.Length > 0 && actual.StartsWith(Value, comparison),
                "$=" => Value.Length > 0 && actual.EndsWith(Value, comparison),
                "*=" => Value.Length > 0 && actual.Contains(Value, comparison),
                "|=" => actual.Equals(Value, comparison) || actual.StartsWith(Value + "-", comparison),
                "~=" => actual.Split(HtmlSpace, StringSplitOptions.RemoveEmptyEntries).Contains(Value,
                    comparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase),
                _ => false
            };
        }
    }

    private readonly record struct Declaration(string Name, string Value, bool Important);

    private sealed record Rule(Compound[] Parts, char[] Combinators, int IdCount, int ClassCount, int TagCount,
        int Order, Declaration[] Declarations, string[] Media)
    {
        internal string Key => Parts[^1].Key;

        internal static int Compare(Rule x, Rule y)
        {
            var compare = x.IdCount.CompareTo(y.IdCount);
            if (compare == 0) compare = x.ClassCount.CompareTo(y.ClassCount);
            if (compare == 0) compare = x.TagCount.CompareTo(y.TagCount);
            return compare == 0 ? x.Order.CompareTo(y.Order) : compare;
        }

        internal static bool TryParse(ReadOnlySpan<char> selector, Declaration[] declarations, string[] media, int order, out Rule rule)
        {
            rule = null!;
            List<Compound> parts = [];
            List<char> combinators = [];
            var ids = 0;
            var classes = 0;
            var tags = 0;
            selector = selector.Trim();
            while (!selector.IsEmpty)
            {
                var offset = FindDelimiter(selector, " \t\r\n\f>+~");
                var current = offset < 0 ? selector : selector[..offset];
                selector = offset < 0 ? [] : selector[offset..].TrimStart();
                if (!Compound.TryParse(current, out var compound)) return false;
                parts.Add(compound);
                ids += compound.Ids.Length;
                classes += compound.Classes.Length + compound.Attributes.Length + compound.Pseudos.Length;
                if (compound.Tag is not null) tags++;
                if (selector.IsEmpty) break;
                var relation = selector[0];
                if (relation is '>' or '+' or '~') selector = selector[1..].TrimStart();
                else relation = ' ';
                if (selector.IsEmpty) return false;
                combinators.Add(relation);
            }
            if (parts.Count == 0) return false;
            rule = new([.. parts], [.. combinators], ids, classes, tags, order, declarations, media);
            return true;
        }
    }

    private sealed record Compound(string? Tag, string[] Ids, string[] Classes, AttributeTest[] Attributes, string[] Pseudos)
    {
        internal string Key => Ids.Length > 0 ? "#" + Ids[0] : Classes.Length > 0 ? "." + Classes[0] : Tag ?? "*";

        internal bool Matches(IElement element, HtmlStyles styles)
        {
            if (Tag is not null && !Tag.Equals(element.LocalName, StringComparison.OrdinalIgnoreCase))
                return false;
            foreach (var id in Ids)
                if (!id.Equals(element.Id, StringComparison.Ordinal))
                    return false;
            foreach (var className in Classes)
                if (!element.ClassList.Contains(className)) return false;
            foreach (var attribute in Attributes)
                if (!attribute.Matches(element)) return false;
            foreach (var pseudo in Pseudos)
                if (!styles.MatchesPseudo(element, pseudo)) return false;
            return true;
        }

        internal static bool TryParse(ReadOnlySpan<char> text, out Compound compound)
        {
            compound = null!;
            List<string>? ids = null;
            List<string>? classes = null;
            List<AttributeTest>? attributes = null;
            List<string>? pseudos = null;
            string? tag = null;
            if (text.IsEmpty) return false;
            if (text[0] == '*') text = text[1..];
            else if (text[0] is not ('#' or '.' or '[' or ':'))
            {
                tag = ReadName(ref text)?.ToLowerInvariant();
                if (tag is null) return false;
            }
            while (!text.IsEmpty)
            {
                var prefix = text[0];
                text = text[1..];
                if (prefix == '[')
                {
                    var end = EndAttribute(text);
                    if (end < 0 || !AttributeTest.TryParse(text[..end].Trim(), out var attribute)) return false;
                    (attributes ??= []).Add(attribute);
                    text = text[(end + 1)..];
                    continue;
                }
                if (prefix is not ('#' or '.' or ':')) return false;
                var name = ReadName(ref text);
                if (name is null) return false;
                if (prefix == '#') (ids ??= []).Add(name);
                else if (prefix == '.') (classes ??= []).Add(name);
                else (pseudos ??= []).Add(name.ToLowerInvariant());
            }
            compound = new(tag, ids?.ToArray() ?? [], classes?.ToArray() ?? [], attributes?.ToArray() ?? [], pseudos?.ToArray() ?? []);
            return true;
        }

        internal static string? ReadName(ref ReadOnlySpan<char> text)
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
