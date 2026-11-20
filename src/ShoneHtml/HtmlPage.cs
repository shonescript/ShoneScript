using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ShoneHtml;

/// <summary>Settings for application-supplied HTML and JavaScript.</summary>
public sealed class HtmlPageOptions
{
    public bool EnableScripts { get; init; } = true;
    public int MaximumLoopIterations { get; init; } = 1_000_000;
    public Action<HtmlScriptError>? OnScriptError { get; init; }
}

/// <summary>A failure in an inline script, event listener, or Promise job.</summary>
public sealed record HtmlScriptError(string Source, Exception Exception);

/// <summary>A parsed page and its JavaScript state. Access the page on its owning UI thread.</summary>
public sealed class HtmlPage
{
    private HtmlScriptHost? _scripts;
    private bool _dirty;

    private HtmlPage(IDocument document) => Document = document;

    public string Title => Document.Title ?? "";

    internal IDocument Document { get; }
    internal long Version { get; private set; }
    internal event Action? Changed;

    /// <summary>Parses HTML and then runs its inline classic scripts in document order.</summary>
    public static HtmlPage Parse(string html, HtmlPageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new HtmlPageOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaximumLoopIterations);

        var page = new HtmlPage(new HtmlParser().ParseDocument(html));
        if (options.EnableScripts)
        {
            var scripts = page.Document.QuerySelectorAll("script");
            for (var index = 0; index < scripts.Length; index++)
            {
                if (!HtmlScriptHost.IsInlineClassicScript(scripts[index]))
                    continue;

                page._scripts ??= new HtmlScriptHost(page, options);
                page._scripts.RunInlineScript(scripts[index].TextContent, index + 1);
            }
            page.PublishChanges();
        }
        return page;
    }

    internal void Click(IElement target) => _scripts?.DispatchClick(target);

    internal void MarkDirty() => _dirty = true;

    internal void PublishChanges()
    {
        if (!_dirty)
            return;

        _dirty = false;
        Version++;
        Changed?.Invoke();
    }
}
