using System.Windows;
using AltManager.App.Services;
using AltManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;

namespace AltManager.App.Views;

/// <summary>
/// Opens roblox.com signed in as one account, by injecting its .ROBLOSECURITY cookie into a
/// per-account persistent WebView2 profile. Each account browses in isolation, so sessions
/// don't collide. The cookie is written fresh on open so a rotated cookie stays current.
/// </summary>
public partial class RobloxBrowserWindow : Window
{
    private const string Home = "https://www.roblox.com/home";
    private readonly string _cookie;
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

        // Inject the auth cookie for roblox.com before navigating, so the first load is already logged in.
        var cm = core.CookieManager;
        var c = cm.CreateCookie(RobloxCookie.Name, _cookie, ".roblox.com", "/");
        c.IsSecure = true;
        c.IsHttpOnly = true;
        cm.AddOrUpdateCookie(c);

        core.Navigate(Home);
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoBack == true) Browser.CoreWebView2.GoBack(); }
    private void Forward_Click(object sender, RoutedEventArgs e) { if (Browser.CoreWebView2?.CanGoForward == true) Browser.CoreWebView2.GoForward(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Navigate(Home);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
