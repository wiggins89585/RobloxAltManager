using System.Windows;
using System.Windows.Threading;
using AltManager.App.Services;
using AltManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;

namespace AltManager.App.Views;

public enum GroupJoinResult { Joined, Pending, Skipped, Stopped }

/// <summary>
/// Joins a group the same way the login window signs in: on the real Roblox page, signed in as
/// the account. Join is pressed automatically; any verification Roblox shows is solved by the
/// user here. Membership is confirmed through the public groups API.
/// </summary>
public partial class GroupJoinWindow : Window
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly RobloxApiClient _api;
    private readonly long _groupId, _userId;
    private readonly string _label, _profileKey;
    private string? _cookie;
    private bool _clicked, _checking;

    public GroupJoinResult Result { get; private set; } = GroupJoinResult.Stopped;

    public GroupJoinWindow(RobloxApiClient api, string cookie, string profileKey, string label,
                           long userId, long groupId, string progress)
    {
        _api = api; _cookie = cookie; _profileKey = profileKey; _label = label;
        _userId = userId; _groupId = groupId;
        InitializeComponent();
        Title = $"Join group {groupId} {progress} — {label}";
        StatusText.Text = $"Joining as {label}… Solve the verification puzzle if it appears.";
        Loaded += async (_, _) => await InitAsync();
        _poll.Tick += async (_, _) => await TickAsync();
        Closed += (_, _) =>
        {
            _poll.Stop();
            Browser.CoreWebView2?.CookieManager.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
        };
    }

    private async Task InitAsync()
    {
        try { await Browser.EnsureCoreWebView2Async(await WebViewEnvironmentFactory.GetForProfileAsync(_profileKey)); }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is not installed.", "WebView2 missing");
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
        core.NavigationStarting += (_, e) => { if (!IsRoblox(e.Uri)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;

        var cm = core.CookieManager;
        cm.DeleteCookiesWithDomainAndPath(RobloxCookie.Name, ".roblox.com", "/");
        var c = cm.CreateCookie(RobloxCookie.Name, _cookie!, ".roblox.com", "/");
        c.IsSecure = true;
        c.IsHttpOnly = true;
        cm.AddOrUpdateCookie(c);
        _cookie = null;

        core.Navigate($"https://www.roblox.com/communities/{_groupId}/about");
        _poll.Start();
    }

    private async Task TickAsync()
    {
        if (_checking || Browser.CoreWebView2 is null) return;
        _checking = true;
        try
        {
            if (await _api.IsInGroupAsync(_userId, _groupId) == true) { Finish(GroupJoinResult.Joined); return; }

            // 2 = awaiting approval, 1 = Join button found (pressed once), 0 = not on the page yet.
            var js = $$"""
                (function (click) {
                  const btns = [...document.querySelectorAll('button')].filter(b => b.offsetParent !== null);
                  const label = b => (b.textContent || '').trim();
                  if (btns.some(b => /^(pending|join request sent|cancel request)/i.test(label(b)))) return 2;
                  const join = btns.find(b => /^join( community| group)?$/i.test(label(b)));
                  if (!join) return 0;
                  if (click) join.click();
                  return 1;
                })({{(_clicked ? "false" : "true")}});
                """;
            var state = (await Browser.CoreWebView2.ExecuteScriptAsync(js)).Trim();
            if (state == "2") { Finish(GroupJoinResult.Pending); return; }
            if (state == "1" && !_clicked)
            {
                _clicked = true;
                StatusText.Text = $"Pressed Join for {_label}. Solve the verification if it appears — this moves on once joined.";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { /* page navigating */ }
        finally { _checking = false; }
    }

    private void Finish(GroupJoinResult r) { Result = r; _poll.Stop(); Close(); }
    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(GroupJoinResult.Skipped);
    private void Stop_Click(object sender, RoutedEventArgs e) => Finish(GroupJoinResult.Stopped);

    private static bool IsRoblox(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "roblox.com" || u.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase));
}
