using System.Windows;
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
    private const string RobloxDarkThemeScript = """
        (() => {
            const host = location.hostname.toLowerCase();
            if (host !== "roblox.com" && !host.endsWith(".roblox.com")) return;

            const styleId = "__alt_manager_theme";
            const css = `
                :root {
                    color-scheme: dark !important;
                    --color-background: #1e1f22 !important;
                    --color-surface: #2b2d31 !important;
                    --color-text: #dbdee1 !important;
                }

                body :where(*) {
                    background-color: transparent !important;
                    border-color: #3f4147 !important;
                    color: #dbdee1 !important;
                }

                html, body, #wrap, #rbx-body, #container-main, .container-main,
                .container-fluid, main, [role="main"] {
                    background-color: #1e1f22 !important;
                    color: #dbdee1 !important;
                }

                .rbx-header, #header, #navContent, .navbar, .navbar-fixed-top,
                nav, [role="navigation"] {
                    background-color: #2b2d31 !important;
                    border-color: #3f4147 !important;
                    color: #dbdee1 !important;
                }

                .game-card, [class*="game-card"], [class*="card"], [class*="tile"],
                [class*="panel"], [class*="banner"], [class*="section"],
                .modal-content, .dropdown-menu, .popover, .well {
                    background-color: #2b2d31 !important;
                    border-color: #3f4147 !important;
                    color: #dbdee1 !important;
                }

                small, .text-muted, .text-secondary, .text-description {
                    color: #949ba4 !important;
                }

                a {
                    color: #a5adff !important;
                }

                input, select, textarea, [contenteditable="true"] {
                    background-color: #1e1f22 !important;
                    border-color: #3f4147 !important;
                    color: #dbdee1 !important;
                }

                button, .btn, [role="button"] {
                    background-color: #383a40 !important;
                    border-color: #3f4147 !important;
                    color: #dbdee1 !important;
                }

                .btn-primary, [class*="btn-primary"] {
                    background-color: #5865f2 !important;
                    border-color: #5865f2 !important;
                    color: #ffffff !important;
                }
            `;

            const injectTheme = () => {
                if (!document.documentElement) return false;
                if (!document.getElementById(styleId)) {
                    const style = document.createElement("style");
                    style.id = styleId;
                    style.textContent = css;
                    (document.head || document.documentElement).appendChild(style);
                }
                return true;
            };

            if (!injectTheme()) {
                const observer = new MutationObserver(() => {
                    if (injectTheme()) observer.disconnect();
                });
                observer.observe(document, { childList: true, subtree: true });
            }
        })();
        """;
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
        await core.AddScriptToExecuteOnDocumentCreatedAsync(RobloxDarkThemeScript);
        core.NavigationStarting += (_, e) =>
        {
            if (!IsTrustedRobloxUri(e.Uri))
                e.Cancel = true;
        };

        // Inject the auth cookie for roblox.com before navigating, so the first load is already logged in.
        var cm = core.CookieManager;
        cm.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
        var c = cm.CreateCookie(RobloxCookie.Name, _cookie!, ".roblox.com", "/");
        c.IsSecure = true;
        c.IsHttpOnly = true;
        cm.AddOrUpdateCookie(c);
        _cookie = null;

        core.Navigate(Home);
    }

    private void ClearSessionCookie()
    {
        var core = Browser.CoreWebView2;
        if (core is null) return;
        core.CookieManager.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
    }

    private static bool IsTrustedRobloxUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
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
