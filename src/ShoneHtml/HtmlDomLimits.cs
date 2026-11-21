using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private int _createdNodes;

    private T AccountNode<T>(T root, bool includeRoot = true) where T : INode
    {
        if (_page.Browser is not { } browser) return root;
        var (_, nodes) = Measure(root, browser.Options.MaximumDocumentDepth, browser.Options.MaximumDocumentNodes - _createdNodes, includeRoot);
        _createdNodes += nodes;
        return root;
    }

    private void CheckInsert(INode parent, INode child, bool includeRoot = true)
    {
        if (_page.Browser is not { } browser) return;
        var depth = 1;
        for (var ancestor = parent.Parent; ancestor is not null; ancestor = ancestor.Parent) depth++;
        var allowance = browser.Options.MaximumDocumentDepth - depth;
        Measure(child, allowance, browser.Options.MaximumDocumentNodes, includeRoot && child.NodeType != NodeType.DocumentFragment);
    }

    private static (int Depth, int Nodes) Measure(INode root, int depthLimit, int sizeLimit, bool includeRoot)
    {
        var stack = new Stack<(INode Node, int Depth)>();
        if (includeRoot) stack.Push((root, 0));
        else
        {
            if (root.ChildNodes.Length > sizeLimit) throw TypeError("Page DOM size limit exceeded");
            foreach (var child in root.ChildNodes) stack.Push((child, 0));
        }
        var count = 0;
        var deepest = 0;
        while (stack.TryPop(out var item))
        {
            if (item.Depth > depthLimit || ++count > sizeLimit || (long)count + stack.Count + item.Node.ChildNodes.Length > sizeLimit)
                throw TypeError("Page DOM size or depth limit exceeded");
            deepest = Math.Max(deepest, item.Depth);
            foreach (var child in item.Node.ChildNodes) stack.Push((child, item.Depth + 1));
        }
        return (deepest, count);
    }

    private INode CreateText(string text)
    {
        if (_page.Browser is { } browser && text.Length > browser.Options.MaximumResponseBytes)
            throw TypeError("Text exceeds the page size limit");
        return AccountNode(_page.Document.CreateTextNode(text));
    }

    private void SetInnerHtml(IElement element, string html)
    {
        if (_page.Browser is not { } browser) { element.InnerHtml = html; return; }
        if (html.Length > browser.Options.MaximumResponseBytes) throw TypeError("Markup exceeds the page size limit");
        var staging = (IElement)element.Clone(false);
        staging.InnerHtml = html;
        CheckInsert(element, staging, includeRoot: false);
        AccountNode(staging, includeRoot: false);
        foreach (var child in element.ChildNodes.ToArray()) element.RemoveChild(child);
        foreach (var child in staging.ChildNodes.ToArray()) element.AppendChild(child);
    }

    private void SetOuterHtml(IElement element, string html)
    {
        if (element.Parent is not { } parent) return;
        if (parent is IDocument)
            throw new JsException(JsValue.From(new JsError("NoModificationAllowedError", "Cannot replace an element whose parent is the document")));
        if (_page.Browser is { } browser && (html.Length > browser.Options.MaximumResponseBytes ||
            Encoding.UTF8.GetByteCount(html) > browser.Options.MaximumResponseBytes))
            throw TypeError("Markup exceeds the page size limit");

        // A fragment parent uses an HTML body as its parsing context, not the replaced element.
        var context = parent as IElement ?? _page.Document.CreateElement("body");
        var staging = _page.Document.CreateDocumentFragment();
        foreach (var child in new HtmlParser().ParseFragment(html, context).ToArray()) staging.AppendChild(child);
        CheckInsert(parent, staging);
        AccountNode(staging, includeRoot: false);
        parent.ReplaceChild(staging, element);
        _page.MarkDirty();
    }
}
