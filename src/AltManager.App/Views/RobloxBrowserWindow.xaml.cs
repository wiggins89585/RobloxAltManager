using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AltManager.App.Services;
using AltManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AltManager.App.Views;

/// <summary>
/// Opens roblox.com signed in as one account, by injecting its .ROBLOSECURITY cookie into a
/// per-account WebView2 profile. Each account browses in isolation, and the auth cookie is
/// removed from the profile when the window closes.
/// </summary>
public partial class RobloxBrowserWindow : Window
{
    private const string Home = "https://www.roblox.com/home";
    private string? _cookie;
    private readonly string _profileKey;
    private readonly (string Server, string? User, string? Pass)? _proxy;
    private readonly List<Tab> _tabs = new();
    private CoreWebView2Environment? _env;
    private Tab? _active;

    /// <summary>One open page. Tabs share the account's profile, so all are signed in.</summary>
    private sealed record Tab(WebView2 View, Button Header, TextBlock Title, FrameworkElement Strip);

    private WebView2? Browser => _active?.View;

    public RobloxBrowserWindow(string cookie, string profileKey, string label, long userId, string? proxy = null)
    {
        _cookie = cookie;
        _profileKey = profileKey;
        _proxy = WebViewEnvironmentFactory.ParseProxy(proxy);
        InitializeComponent();
        Title = $"Roblox — {label} (ID: {userId})";
        StatusText.Text = $"Signed in as {label} · ID {userId}";
        StateChanged += (_, _) =>
        {
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
            MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        };
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => ClearSessionCookie();
    }

    private async Task InitAsync()
    {
        try
        {
            _env = await WebViewEnvironmentFactory.GetForProfileAsync(_profileKey, _proxy is { } p ? $"{p.Server}" : null);
            await AddTabAsync(Home, injectCookie: true);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            WebViewEnvironmentFactory.PromptInstallRuntime(this);
            Close();
        }
    }

    private async Task AddTabAsync(string url, bool injectCookie = false)
    {
        var title = new TextBlock { Text = "New tab", MaxWidth = 150, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new Button { Content = title, Padding = new Thickness(8, 2, 8, 2) };
        var close = new Button { Content = "×", Padding = new Thickness(6, 2, 6, 2), ToolTip = "Close tab (Ctrl+W)" };
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 4, 0) };
        strip.Children.Add(header);
        strip.Children.Add(close);
        TabStrip.Children.Add(strip);

        var view = new WebView2();
        Pages.Children.Add(view);
        var tab = new Tab(view, header, title, strip);
        _tabs.Add(tab);
        header.Click += (_, _) => Activate(tab);
        close.Click += (_, _) => CloseTab(tab);
        Activate(tab);

        await view.EnsureCoreWebView2Async(_env);
        var core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;

        // Supply proxy credentials for an authenticated proxy (host:port:user:pass).
        if (_proxy is { User: { } pu, Pass: { } pp })
            core.BasicAuthenticationRequested += (_, e) =>
            {
                e.Response.UserName = pu;
                e.Response.Password = pp;
            };
        core.NavigationStarting += (_, e) =>
        {
            if (!IsTrustedRobloxUri(e.Uri))
                e.Cancel = true;
        };
        core.SourceChanged += (_, _) => { if (tab == _active) SyncAddress(); };
        core.DocumentTitleChanged += (_, _) => title.Text = core.DocumentTitle;

        // Links that open a new tab or window land here. Without this they get a bare popup.
        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            if (IsTrustedRobloxUri(e.Uri)) await AddTabAsync(e.Uri);
        };

        if (injectCookie)
        {
            // Inject the auth cookie for roblox.com before navigating, so the first load is already logged in.
            var cm = core.CookieManager;
            cm.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
            var c = cm.CreateCookie(RobloxCookie.Name, _cookie!, ".roblox.com", "/");
            c.IsSecure = true;
            c.IsHttpOnly = true;
            cm.AddOrUpdateCookie(c);
            _cookie = null;
        }

        core.Navigate(url);
        if (tab == _active) SyncAddress();
    }

    private void Activate(Tab tab)
    {
        _active = tab;
        foreach (var t in _tabs)
        {
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
            if (t == tab) t.Header.SetResourceReference(BackgroundProperty, "Selection");
            else t.Header.ClearValue(BackgroundProperty);
        }
        SyncAddress();
    }

    private void CloseTab(Tab tab)
    {
        if (_tabs.Count == 1) { Close(); return; }

        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.Strip);
        Pages.Children.Remove(tab.View);
        tab.View.Dispose();
        if (tab == _active) Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await AddTabAsync(Home);

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.T) { e.Handled = true; await AddTabAsync(Home); }
        else if (e.Key == Key.W && _active is { } tab) { e.Handled = true; CloseTab(tab); }
    }

    private void SyncAddress()
    {
        var source = Browser?.CoreWebView2?.Source;
        if (source is not null)
            UrlBox.Text = source;
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var address = UrlBox.Text.Trim();
        if (!address.Contains("://", StringComparison.Ordinal))
            address = $"https://{address}";

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !IsTrustedRobloxUri(uri))
        {
            MessageBox.Show(this, "Enter an HTTPS URL on roblox.com.", "Unsupported address",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SyncAddress();
            return;
        }

        Browser?.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }

    private void ClearSessionCookie()
    {
        var core = _tabs.Select(t => t.View.CoreWebView2).FirstOrDefault(c => c is not null);
        if (core is null) return;
        core.CookieManager.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
    }

    private static bool IsTrustedRobloxUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTrustedRobloxUri(uri);

    private static bool IsTrustedRobloxUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));

    private void Back_Click(object sender, RoutedEventArgs e) { if (Browser?.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Browser?.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Browser?.CoreWebView2?.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) => Browser?.CoreWebView2?.Navigate(Home);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
