using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using ShoneJs.Runtime;

namespace ShoneHtml;

internal sealed partial class HtmlScriptHost
{
    private int _createdNodes;

    private T AccountNode<T>(T root, bool includeRoot = true) where T : INode
    {
        if (BrowserOptions is not { } options) return root;
        _createdNodes += Measure(root, options.MaximumDocumentDepth, options.MaximumDocumentNodes - _createdNodes, includeRoot);
        return root;
    }

    private void CheckInsert(INode parent, INode child, bool includeRoot = true)
    {
        if (BrowserOptions is not { } options) return;
        var depth = 1;
        for (var ancestor = parent.Parent; ancestor is not null; ancestor = ancestor.Parent) depth++;
        var allowance = options.MaximumDocumentDepth - depth;
        Measure(child, allowance, options.MaximumDocumentNodes, includeRoot && child.NodeType != NodeType.DocumentFragment);
    }

    private Exception BudgetError(string reason)
    {
        if (Renderer is null) return TypeError(reason);
        TerminateRenderer(reason);
        return new RendererBudgetException(reason);
    }

    private int Measure(INode root, int depthLimit, int sizeLimit, bool includeRoot)
    {
        var renderer = Renderer;
        var checkContent = renderer is not null && root is IDocument;
        long remainingBytes = checkContent
            ? Math.Max(renderer!.Options.MaximumResponseBytes, renderer.Options.MaximumPageResourceBytes) : 0;
        var stack = new Stack<(INode Node, int Depth)>();
        var count = 0;
        if (includeRoot) stack.Push((root, 0));
        else PushChildren(root, 0);
        while (stack.TryPop(out var item))
        {
            if (item.Depth > depthLimit || ++count > sizeLimit) throw BudgetError("Page DOM size or depth limit exceeded");
            if (checkContent)
            {
                _page.Lifetime.ThrowIfCancellationRequested();
                if (item.Node.NodeValue is { } value) AccountText(value);
                if (item.Node is IHtmlInputElement input) AccountText(input.Value);
                else if (item.Node is IHtmlTextAreaElement textarea) AccountText(textarea.Value);
                if (item.Node is IElement element)
                {
                    AccountText(element.NodeName);
                    foreach (var attribute in element.Attributes)
                    {
                        AccountText(attribute.Name);
                        AccountText(attribute.Value);
                    }
                }
            }
            PushChildren(item.Node, item.Depth + 1);
        }
        return count;

        void PushChildren(INode node, int depth)
        {
            var template = renderer is not null ? (node as IHtmlTemplateElement)?.Content : null;
            if ((long)count + stack.Count + node.ChildNodes.Length + (template?.ChildNodes.Length ?? 0) > sizeLimit)
                throw BudgetError("Page DOM size limit exceeded");
            foreach (var child in node.ChildNodes) stack.Push((child, depth));
            if (template is not null)
                foreach (var child in template.ChildNodes) stack.Push((child, depth));
        }

        void AccountText(string text)
        {
            if (text.Length > remainingBytes) throw BudgetError("Page DOM content limit exceeded");
            remainingBytes -= Encoding.UTF8.GetByteCount(text);
            if (remainingBytes < 0) throw BudgetError("Page DOM content limit exceeded");
        }
    }

    private void CheckDomText(string text, string source, bool checkUtf8 = false)
    {
        if (BrowserOptions is { MaximumResponseBytes: var limit } && (text.Length > limit ||
            (checkUtf8 || Renderer is not null) && Encoding.UTF8.GetByteCount(text) > limit))
            throw BudgetError($"{source} exceeds the page size limit");
    }

    private INode CreateText(string text)
    {
        CheckDomText(text, "Text");
        return AccountNode(_page.Document.CreateTextNode(text));
    }

    private void SetInnerHtml(IElement element, string html)
    {
        if (BrowserOptions is null) { element.InnerHtml = html; return; }
        CheckDomText(html, "Markup");
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
        CheckDomText(html, "Markup", checkUtf8: true);

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
