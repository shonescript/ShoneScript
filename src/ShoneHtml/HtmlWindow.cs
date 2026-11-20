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

        var window = new Window()
            .Title(page.Title)
            .Resizable(680, 480, minWidth: 300, minHeight: 240);
        var owner = new WeakReference<Window>(window);
        return window.Content(new HtmlView(page)
        {
            SynchronizeTitle = host =>
            {
                if (owner.TryGetTarget(out var original) && ReferenceEquals(original, host))
                    host.Title = page.Title;
            }
        });
    }
}
