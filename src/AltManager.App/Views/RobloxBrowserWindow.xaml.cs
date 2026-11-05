using System.Windows;
using System.Windows.Input;
using AltManager.App.Services;
using AltManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;

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

    public RobloxBrowserWindow(string cookie, string profileKey, string label, long userId)
    {
        _cookie = cookie;
        _profileKey = profileKey;
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
            var env = await WebViewEnvironmentFactory.GetForProfileAsync(_profileKey);
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is not installed.", "WebView2 missing");
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsTrustedRobloxUri(e.Uri))
                e.Cancel = true;
        };
        core.SourceChanged += (_, _) => SyncAddress();

        // Inject the auth cookie for roblox.com before navigating, so the first load is already logged in.
        var cm = core.CookieManager;
        cm.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
        var c = cm.CreateCookie(RobloxCookie.Name, _cookie!, ".roblox.com", "/");
        c.IsSecure = true;
        c.IsHttpOnly = true;
        cm.AddOrUpdateCookie(c);
        _cookie = null;

        core.Navigate(Home);
        SyncAddress();
    }

    private void SyncAddress()
    {
        var source = Browser.CoreWebView2?.Source;
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

        Browser.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }

    private void ClearSessionCookie()
    {
        var core = Browser.CoreWebView2;
        if (core is null) return;
        core.CookieManager.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
    }

    private static bool IsTrustedRobloxUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTrustedRobloxUri(uri);

    private static bool IsTrustedRobloxUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "www.roblox.com" or "roblox.com" or "auth.roblox.com";

    private void Back_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Navigate(Home);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
