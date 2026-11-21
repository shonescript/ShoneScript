using System.Runtime.InteropServices;
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using ShoneHtml;

internal static class BrowserWindow
{
    internal static Window Create(string? initialAddress, string startPage, HtmlBrowserScriptMode scriptMode)
    {
        var window = new Window()
            .Title("ShoneHtml")
            .Resizable(960, 680, minWidth: 420, minHeight: 260);
        var browser = new HtmlBrowser(new HtmlBrowserOptions
        {
            ScriptMode = scriptMode,
            GetDisplayMetrics = () => DisplayMetrics(window)
        }, new UiContext());
        var address = new TextBox().Placeholder("https://").Column(4);
        var scriptStatus = scriptMode switch
        {
            HtmlBrowserScriptMode.Disabled => "Scripts disabled",
            HtmlBrowserScriptMode.TrustedInProcess => "Trusted scripts (in process)",
            _ => ""
        };
        var status = new TextBlock().Text("Ready").Margin(10, 4).DockBottom();
        var closed = false;
        var back = ToolbarButton("\u2190", "Back (Alt+Left)", () => Run(browser.GoBackAsync)).Column(0);
        var forward = ToolbarButton("\u2192", "Forward (Alt+Right)", () => Run(browser.GoForwardAsync)).Column(1);
        var refresh = ToolbarButton("\u21BB", "Reload (F5)", () => Run(browser.RefreshAsync)).Column(2);
        var stop = ToolbarButton("\u00D7", "Stop (Escape)", browser.Stop).Column(3);
        var go = ToolbarButton("\u21B5", "Go (Enter)", Navigate).Column(5);
        var view = new HtmlView(browser, HtmlPage.Parse(startPage,
            new HtmlPageOptions { EnableScripts = scriptMode != HtmlBrowserScriptMode.Disabled }));
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
            var navigationStatus = browser.Status;
            if (scriptStatus.Length > 0)
                status.Text = navigationStatus.Length > 0 ? $"{scriptStatus} | {navigationStatus}" : scriptStatus;
            else
                status.Text = navigationStatus.Length > 0 ? navigationStatus : "Ready";
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

    private static HtmlDisplayMetrics DisplayMetrics(Window window)
    {
        var scale = window.DpiScale;
        if (OperatingSystem.IsWindows() && window.Handle != 0)
        {
            var monitor = MonitorFromWindow(window.Handle, 2); // nearest monitor
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != 0 && GetMonitorInfo(monitor, ref info))
                return new((int)Math.Round((info.Bounds.Right - info.Bounds.Left) / scale),
                    (int)Math.Round((info.Bounds.Bottom - info.Bounds.Top) / scale), scale);
        }
        return new(0, 0, scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Bounds, WorkArea; public int Flags; }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

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
