using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using ShoneHtml;

internal static class BrowserWindow
{
    internal static Window Create(string? initialAddress, string startPage, bool trustedScripts)
    {
        var browser = new HtmlBrowser(new HtmlBrowserOptions { EnableTrustedScripts = trustedScripts }, new UiContext());
        var window = new Window()
            .Title("ShoneHtml")
            .Resizable(960, 680, minWidth: 420, minHeight: 260);
        var address = new TextBox().Placeholder("https://").Column(4);
        var status = new TextBlock().Text("Ready").Margin(10, 4).DockBottom();
        var closed = false;
        var back = ToolbarButton("\u2190", "Back (Alt+Left)", () => Run(browser.GoBackAsync)).Column(0);
        var forward = ToolbarButton("\u2192", "Forward (Alt+Right)", () => Run(browser.GoForwardAsync)).Column(1);
        var refresh = ToolbarButton("\u21BB", "Reload (F5)", () => Run(browser.RefreshAsync)).Column(2);
        var stop = ToolbarButton("\u00D7", "Stop (Escape)", browser.Stop).Column(3);
        var go = ToolbarButton("\u21B5", "Go (Enter)", Navigate).Column(5);
        var view = new HtmlView(browser, HtmlPage.Parse(startPage));
        string? displayedAddress = null;
        long displayedNavigation = -1;

        window.Content(new DockPanel().Children(
            new Grid().Columns("34,34,34,34,*,34").Spacing(5).Margin(8).DockTop()
                .Children(back, forward, refresh, stop, address, go),
            status,
            view));

        window.PreviewKeyDown += e =>
        {
            if ((e.PrimaryKey && e.Key == Key.L) || e.Key == Key.F6)
            {
                address.Focus();
                address.SelectAll();
            }
            else if (address.IsFocused && e.Key == Key.Enter)
                Navigate();
            else if (e.AltKey && e.Key == Key.Left)
                Run(browser.GoBackAsync);
            else if (e.AltKey && e.Key == Key.Right)
                Run(browser.GoForwardAsync);
            else if (e.Key == Key.F5 || (e.PrimaryKey && e.Key == Key.R))
                Run(browser.RefreshAsync);
            else if (e.Key == Key.Escape && browser.IsLoading)
                browser.Stop();
            else
                return;
            e.Handled = true;
        };
        browser.Changed += RefreshChrome;
        view.PageChanged += RefreshTitle;
        window.Loaded += () =>
        {
            RefreshChrome();
            if (!string.IsNullOrWhiteSpace(initialAddress))
            {
                address.Text = initialAddress;
                Navigate();
            }
            else
                address.Focus();
        };
        window.Closed += () =>
        {
            closed = true;
            browser.Changed -= RefreshChrome;
            view.PageChanged -= RefreshTitle;
            browser.Dispose();
        };
        return window;

        void RefreshChrome()
        {
            if (closed)
                return;
            back.IsEnabled = browser.CanGoBack;
            forward.IsEnabled = browser.CanGoForward;
            refresh.IsEnabled = browser.Address is not null;
            stop.IsEnabled = browser.IsLoading;
            status.Text = browser.Status.Length > 0 ? browser.Status : trustedScripts ? "Trusted scripts enabled" : "Ready";
            if (browser.Address?.AbsoluteUri is { } current &&
                (current != displayedAddress || displayedNavigation != browser.NavigationVersion))
            {
                displayedAddress = current;
                displayedNavigation = browser.NavigationVersion;
                address.Text = current;
            }
            RefreshTitle();
        }

        void RefreshTitle()
        {
            if (!closed)
            {
                var title = view.Page.Title;
                window.Title = string.IsNullOrWhiteSpace(title) ? "ShoneHtml" : $"{title} | ShoneHtml";
            }
        }

        void Navigate()
        {
            var requested = address.Text.Trim();
            if (requested.Length == 0)
                return;
            Run(() => browser.NavigateAsync(requested));
        }

        async void Run(Func<Task> navigate)
        {
            try
            {
                await navigate();
            }
            catch (Exception error)
            {
                if (!closed)
                    UiContext.Dispatcher.BeginInvoke(() => { if (!closed) status.Text = error.Message; });
            }
        }
    }

    private static Button ToolbarButton(string symbol, string help, Action clicked)
        => new Button().Content(symbol).FontSize(18).OnClick(clicked)
            .ToolTip(new TextBlock().Text(help));

    private sealed class UiContext : SynchronizationContext
    {
        internal static IDispatcher Dispatcher => Application.Current.Dispatcher ??
            throw new InvalidOperationException("No UI dispatcher is available.");

        public override void Post(SendOrPostCallback callback, object? state)
            => Dispatcher.BeginInvoke(() => Execute(callback, state));

        public override void Send(SendOrPostCallback callback, object? state)
        {
            if (Dispatcher.IsOnUIThread)
                Execute(callback, state);
            else
                Dispatcher.Invoke(() => Execute(callback, state));
        }

        private void Execute(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            finally { SetSynchronizationContext(previous); }
        }
    }
}
