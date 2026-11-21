using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace ShoneHtml;

/// <summary>Creates standalone MewUI windows for HTML pages.</summary>
public static class HtmlWindow
{
    public static Window Create(string html, HtmlPageOptions? options = null)
        => Create(HtmlPage.Parse(html, options));

    public static Window Create(HtmlPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return CreateCore(page.Title, synchronize => new HtmlView(page) { SynchronizeTitle = synchronize });
    }

    /// <summary>Creates a live-page window for a caller-owned, UI-thread browser session.</summary>
    public static Window CreateBrowser(HtmlBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return CreateCore(browser.Page?.Title ?? "ShoneHtml", synchronize =>
            new HtmlView(browser) { SynchronizeTitle = synchronize });
    }

    private static Window CreateCore(string title, Func<Action<Window>, HtmlView> createView)
    {
        var window = new Window()
            .Title(title)
            .Resizable(680, 480, minWidth: 300, minHeight: 240);
        var owner = new WeakReference<Window>(window);
        HtmlView? view = null;
        view = createView(host =>
        {
            if (owner.TryGetTarget(out var original) && ReferenceEquals(original, host) && view is not null)
                host.Title = string.IsNullOrEmpty(view.Page.Title) ? title : view.Page.Title;
        });
        return window.Content(view);
    }
}
