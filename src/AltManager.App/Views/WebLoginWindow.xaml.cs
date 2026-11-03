using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AltManager.App.Services;
using AltManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;

namespace AltManager.App.Views;

/// <summary>
/// Manual login fallback for when Roblox demands a FunCaptcha. Captures the
/// .ROBLOSECURITY cookie as soon as it is issued, then removes it from the
/// embedded profile WITHOUT calling the logout endpoint (which would invalidate
/// the very cookie we just captured). Cache/IndexedDB stay, so the next login
/// still looks like a returning device.
/// </summary>
public partial class WebLoginWindow : Window
{
    private const string LoginUrl = "https://www.roblox.com/login";
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly string? _customUserAgent;
    private bool _captured;
    private bool _checking;
    private (string User, string Pass)? _credentials;
    private readonly bool _autoSubmit;
    private readonly bool _abortOnChallenge;
    private bool _autofilled;
    private bool _submitted;

    /// <summary>
    /// Bulk auto-login hit a verification/2-step challenge (e.g. an email code) it can't
    /// answer, so this account was set aside for the user to finish manually.
    /// </summary>
    public bool NeedsManual { get; private set; }
    private readonly DispatcherTimer _blankCheck = new() { Interval = TimeSpan.FromSeconds(12) };
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _retries, _retrySecondsLeft;
    private string _retryReason = "";

    /// <summary>The captured cookie value, or null if the user closed the window.</summary>
    public string? CapturedCookie { get; private set; }

    /// <param name="customUserAgent">
    /// Optional override. Leave null to use the runtime's own UA — a UA that
    /// disagrees with the Sec-CH-UA client hints is a common reason Arkose escalates.
    /// </param>
    /// <param name="autoSubmit">Fill AND click Log In (bulk). False = prefill only, user clicks (manual).</param>
    /// <param name="abortOnChallenge">Bulk auto only: close with <see cref="NeedsManual"/> if a verification challenge appears.</param>
    public WebLoginWindow(string? customUserAgent = null, (string User, string Pass)? credentials = null,
                          string? progress = null, bool autoSubmit = true, bool abortOnChallenge = true)
    {
        _customUserAgent = customUserAgent;
        _credentials = credentials;
        _autoSubmit = autoSubmit;
        _abortOnChallenge = abortOnChallenge;
        InitializeComponent();
        if (credentials is { } c)
        {
            Title = $"Roblox Login {progress} — {c.User}";
            StatusText.Text = autoSubmit
                ? $"Signing in as {c.User}… Solve the verification puzzle if it appears."
                : $"Manual sign-in for {c.User}. The password is filled in — press Log In and enter the email/verification code.";
        }
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) =>
        {
            _poll.Stop();
            _blankCheck.Stop();
            _retryTimer.Stop();
            _credentials = null;
        };
        _poll.Tick += async (_, _) => { await TryCaptureAsync(); await TryDetectChallengeAsync(); };
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await WebViewEnvironmentFactory.GetAsync();
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "The Microsoft Edge WebView2 Runtime is not installed.", "WebView2 missing");
            Close();
            return;
        }

        var core = Browser.CoreWebView2;
        var s = core.Settings;

        s.UserAgent = string.IsNullOrWhiteSpace(_customUserAgent) ? StripWebViewMarkers(s.UserAgent) : _customUserAgent;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;

        // Tracking prevention partitions/block third-party storage → Arkose iframe re-challenges.
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;

        core.NavigationStarting += (_, e) =>
        {
            if (!IsTrustedRobloxUri(e.Uri))
            {
                e.Cancel = true;
                StatusText.Text = "Blocked navigation outside Roblox.";
            }
        };
        // Keep Roblox verification popups in-frame, but never send credentials to other sites.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsTrustedRobloxUri(e.Uri))
                core.Navigate(e.Uri);
        };
        core.NavigationCompleted += async (_, e) =>
        {
            // 429 = Roblox is throttling logins from this IP; a failed load also leaves a blank page.
            if (e.HttpStatusCode == 429 ||
                (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled))
            {
                ScheduleRetry(e.HttpStatusCode == 429 ? "Roblox is rate-limiting logins" : "The login page failed to load");
                return;
            }
            await TryCaptureAsync();
            await TryAutofillAsync();
            _blankCheck.Stop();
            _blankCheck.Start();
        };
        _blankCheck.Tick += async (_, _) =>
        {
            _blankCheck.Stop();
            if (_captured || _retryTimer.IsEnabled || Browser.CoreWebView2 is null) return;
            // A throttled login page often loads "successfully" but never renders anything.
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(
                "(document.body ? document.body.innerText.trim().length : 0) + (document.querySelector('#login-username, iframe') ? 1000 : 0)");
            if (int.TryParse(json, out var score) && score < 20)
                ScheduleRetry("The login page came up blank (likely rate-limited)");
        };
        _retryTimer.Tick += (_, _) =>
        {
            if (--_retrySecondsLeft > 0) { StatusText.Text = $"{_retryReason}. Retrying in {_retrySecondsLeft}s… (or press Reload)"; return; }
            _retryTimer.Stop();
            Navigate();
        };

        // Start from a clean session: drop any leftover roblox cookie from a prior login.
        await ClearRobloxSessionCookiesAsync();

        core.Navigate(LoginUrl);
        _poll.Start(); // the login form is an SPA; a successful login doesn't always fire a navigation
    }

    /// <summary>Back off before reloading: 30s, 60s, 120s, then 240s max. Hammering Roblox only extends the limit.</summary>
    private void ScheduleRetry(string reason)
    {
        if (_captured || _retryTimer.IsEnabled) return;
        _blankCheck.Stop();
        _retries++;
        _retryReason = reason;
        _retrySecondsLeft = Math.Min(30 * (1 << (_retries - 1)), 240);
        StatusText.Text = $"{reason}. Retrying in {_retrySecondsLeft}s… (or press Reload)";
        _retryTimer.Start();
    }

    private void Navigate()
    {
        _autofilled = false; // the fresh page needs filling again
        _submitted = false;
        StatusText.Text = _credentials is { } c
            ? $"Signing in as {c.User}… Solve the verification puzzle if it appears."
            : "Log in normally. Solve the verification puzzle if it appears.";
        Browser.CoreWebView2?.Navigate(LoginUrl);
    }

    private async Task TryCaptureAsync()
    {
        // Timer ticks and NavigationCompleted can overlap; only one check runs at a time.
        if (_captured || _checking || Browser.CoreWebView2 is null) return;
        _checking = true;
        CoreWebView2Cookie? sec;
        try
        {
            var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            sec = cookies.FirstOrDefault(c => c.Name == RobloxCookie.Name && !string.IsNullOrEmpty(c.Value));
        }
        finally { _checking = false; }
        if (sec is null || _captured) return;

        _captured = true;
        _poll.Stop();
        CapturedCookie = sec.Value;
        StatusText.Text = "Signed in — saving account…";

        await ClearRobloxSessionCookiesAsync();
        DialogResult = true;
    }

    /// <summary>
    /// Bulk mode: fill the login form once and press Log In. The form is a React SPA,
    /// so values go through the native setter + input events, and we wait for the fields to mount.
    /// </summary>
    private async Task TryAutofillAsync()
    {
        if (_credentials is not { } c || _autofilled || _captured || Browser.CoreWebView2 is null) return;
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var source) ||
            !IsTrustedRobloxUri(source) ||
            !source.AbsolutePath.Equals("/login", StringComparison.OrdinalIgnoreCase))
            return;
        _autofilled = true;

        var script = $$"""
            (function (u, p) {
              const set = (el, v) => {
                Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v);
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
              };
              let tries = 0;
              const t = setInterval(() => {
                const a = document.querySelector('#login-username'),
                      b = document.querySelector('#login-password'),
                      btn = document.querySelector('#login-button');
                if (a && b && btn) { clearInterval(t); set(a, u); set(b, p); if (submit) setTimeout(() => btn.click(), 300); }
                else if (++tries > 60) clearInterval(t);
              }, 250);
            })({{JsonSerializer.Serialize(c.User)}}, {{JsonSerializer.Serialize(c.Pass)}}, {{(_autoSubmit ? "true" : "false")}});
            """;
        await Browser.CoreWebView2.ExecuteScriptAsync(script);
        _credentials = null;
        if (_autoSubmit) _submitted = true;
    }

    private static bool IsTrustedRobloxUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTrustedRobloxUri(uri);

    private static bool IsTrustedRobloxUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "www.roblox.com" or "roblox.com" or "auth.roblox.com";

    /// <summary>
    /// After an auto-submit, watch for a verification / 2-step page (email code, etc.) that the
    /// autofill can't answer. When seen, set <see cref="NeedsManual"/> and close so the bulk run
    /// queues this account for manual sign-in at the end.
    /// </summary>
    private async Task TryDetectChallengeAsync()
    {
        if (!_submitted || !_abortOnChallenge || _captured || NeedsManual || Browser.CoreWebView2 is null) return;

        const string probe = """
            (function () {
              if (document.querySelector('#login-password')) return 0;   // still on the login form
              const txt = (document.body ? document.body.innerText : '').toLowerCase();
              const kw = ['verification', '2-step', 'two-step', '2sv', 'security code', 'enter the code',
                          'verify it’s you', "verify it's you", 'check your email'];
              const hasInput = !!document.querySelector('input[type=text],input[type=tel],input[type=number],input[autocomplete=one-time-code]');
              return (hasInput && kw.some(k => txt.includes(k))) ? 1 : 0;
            })();
            """;
        var json = await Browser.CoreWebView2.ExecuteScriptAsync(probe);
        if (json.Trim() != "1" || _captured) return;

        NeedsManual = true;
        _poll.Stop();
        DialogResult = false; // closed without a cookie; bulk reads NeedsManual
    }

    /// <summary>Deletes only the auth cookie(s) locally. Does NOT hit /logout.</summary>
    private async Task ClearRobloxSessionCookiesAsync()
    {
        var mgr = Browser.CoreWebView2.CookieManager;
        foreach (var c in await mgr.GetCookiesAsync("https://www.roblox.com"))
            if (c.Name is RobloxCookie.Name or "RBXSessionTracker")
                mgr.DeleteCookie(c);
    }

    /// <summary>WebView2's UA equals desktop Edge's; we only strip an embedding marker if one is present.</summary>
    private static string StripWebViewMarkers(string ua) =>
        Regex.Replace(ua, @"\s+WebView2/\S+", "").Trim();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _retryTimer.Stop();
        Navigate();
    }
}
