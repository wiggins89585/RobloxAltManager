using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using AltManager.App.Services;
using AltManager.Core.Roblox;
using Microsoft.Web.WebView2.Core;

namespace AltManager.App.Views;

/// <summary>
/// Opens Roblox's sign-up page and fills in the birthday, username, password and gender the user
/// chose, so the only thing left to do in the browser is solve the CAPTCHA. We never automate the
/// puzzle. Roblox signs the new account in on success, so this captures the .ROBLOSECURITY cookie
/// the same way the login window does, then removes it from the embedded profile.
/// </summary>
public partial class SignupWindow : Window
{
    private const string SignupUrl = "https://www.roblox.com/CreateAccount";
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly string? _customUserAgent;
    private SignupInput _input;       // the username can change if Roblox rejects it
    private bool _captured;
    private bool _checking;
    private int _usernameTries;

    /// <summary>The captured cookie value, or null if the user closed the window before finishing.</summary>
    public string? CapturedCookie { get; private set; }

    /// <summary>The username actually used, which may differ from the one asked for if Roblox rejected it.</summary>
    public string FinalUsername => _input.Username;

    public SignupWindow(SignupInput input, string? customUserAgent = null)
    {
        _input = input;
        _customUserAgent = customUserAgent;
        InitializeComponent();
        Loaded += async (_, _) => await InitAsync();
        Closed += (_, _) => _poll.Stop();
        _poll.Tick += async (_, _) => { await TryCaptureAsync(); await TryFillAsync(); };
    }

    private async Task InitAsync()
    {
        try
        {
            await Browser.EnsureCoreWebView2Async(await WebViewEnvironmentFactory.GetAsync());
        }
        catch (WebView2RuntimeNotFoundException)
        {
            WebViewEnvironmentFactory.PromptInstallRuntime(this);
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

        // Tracking prevention partitions third-party storage → the Arkose CAPTCHA iframe re-challenges.
        core.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;

        // We sign in with passwords, so stop Roblox from offering a passkey — otherwise Windows keeps
        // popping its "save a passkey" dialog, which can't always be cancelled.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(DisablePasskeysScript);

        core.NavigationStarting += (_, e) =>
        {
            if (!IsTrustedRobloxUri(e.Uri))
            {
                e.Cancel = true;
                StatusText.Text = "Blocked navigation outside Roblox.";
            }
        };
        // Keep Roblox pop-ups in-frame; never follow a link off Roblox.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsTrustedRobloxUri(e.Uri)) core.Navigate(e.Uri);
        };
        core.NavigationCompleted += async (_, _) =>
        {
            await TryCaptureAsync();
        };

        // Start logged out, so the sign-up form shows instead of a signed-in home page.
        await ClearRobloxSessionCookiesAsync();
        core.Navigate(SignupUrl);
        _poll.Start();
    }

    private async Task TryCaptureAsync()
    {
        // Timer ticks can overlap; only one check runs at a time.
        if (_captured || _checking || Browser.CoreWebView2 is null) return;
        _checking = true;
        CoreWebView2Cookie? sec = null;
        try
        {
            foreach (var domain in new[] { "https://www.roblox.com", "https://roblox.com", "https://web.roblox.com" })
            {
                var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(domain);
                sec = cookies.FirstOrDefault(c => c.Name == RobloxCookie.Name && !string.IsNullOrEmpty(c.Value));
                if (sec is not null) break;
            }
        }
        finally { _checking = false; }
        if (sec is null || _captured) return;

        _captured = true;
        _poll.Stop();
        CapturedCookie = sec.Value;
        StatusText.Text = "Account created — saving…";

        await ClearRobloxSessionCookiesAsync();
        DialogResult = true;
    }

    /// <summary>
    /// Fills the sign-up form each tick (so it survives a multi-step flow), then clicks Sign Up /
    /// Continue once per step — never while a CAPTCHA is up, and never twice for the same form, so it
    /// can't double-submit (which is what made Roblox say "please try again").
    /// </summary>
    private async Task TryFillAsync()
    {
        if (_captured || Browser.CoreWebView2 is null) return;
        if (!Uri.TryCreate(Browser.CoreWebView2.Source, UriKind.Absolute, out var src) || !IsTrustedRobloxUri(src))
            return;

        var script = $$"""
            (function (data) {
              // Type into a field the way a real keystroke would: execCommand('insertText') fires a
              // native beforeinput/input that React's controlled inputs accept and keep (setting
              // .value directly often gets wiped on the next React render). Falls back to the setter.
              const setInput = (el, v) => {
                if (!el || el.value === v) return;
                el.focus();
                let ok = false;
                if (document.activeElement === el) { // only type when focus really landed on el,
                  try { document.execCommand('selectAll', false, null); } catch (e) { } // else insertText
                  try { ok = document.execCommand('insertText', false, v); } catch (e) { } // hits the wrong field
                }
                if (!ok || el.value !== v) {
                  const last = el.value;
                  const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                  Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, v);
                  if (el._valueTracker) el._valueTracker.setValue(last);
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                }
              };
              const visible = el => el && el.offsetParent !== null && !el.disabled;
              // A field is "settled" once it has held the right value for ~1s, so we submit only after
              // React has committed it — not in the same instant we type it.
              const settled = (el, key, target) => {
                if (!el || el.value !== target) { window[key] = 0; return false; }
                if (!window[key]) window[key] = Date.now();
                return Date.now() - window[key] > 1000;
              };

              // ----- Birthday: three <select>s. Identify by label/id, else by option count. -----
              const selects = [...document.querySelectorAll('select')].filter(visible);
              const pick = (kind) => {
                const by = selects.find(s => new RegExp(kind, 'i').test(
                  (s.id||'') + ' ' + (s.name||'') + ' ' + (s.getAttribute('aria-label')||'')));
                if (by) return by;
                if (kind === 'month') return selects.find(s => s.options.length >= 12 && s.options.length <= 13);
                if (kind === 'day')   return selects.find(s => s.options.length >= 28 && s.options.length <= 32);
                if (kind === 'year')  return selects.find(s => s.options.length > 32);
                return null;
              };
              const setSelect = (sel, wanted) => {
                if (!sel) return;
                const opts = [...sel.options];
                const want = String(wanted);
                let o = opts.find(o => o.value === want || o.text.trim() === want ||
                                       parseInt(o.value, 10) === wanted || parseInt(o.text, 10) === wanted);
                if (!o && typeof wanted === 'string')   // month name → match first 3 letters
                  o = opts.find(o => o.text.trim().toLowerCase().startsWith(wanted.slice(0,3).toLowerCase()));
                if (o) { sel.value = o.value; sel.dispatchEvent(new Event('change', { bubbles: true })); }
              };
              setSelect(pick('month'), data.monthName);
              setSelect(pick('day'), data.day);
              setSelect(pick('year'), data.year);

              // ----- Username / password -----
              const user = document.querySelector('#signup-username, input[name="signupUsername"]') ||
                [...document.querySelectorAll('input')].find(i => visible(i) &&
                  i.type !== 'password' && /user|name/i.test((i.placeholder||'')+(i.name||'')+(i.id||'')+(i.getAttribute('aria-label')||'')));
              // Must be VISIBLE — a hidden #signup-password on the first step would otherwise make us
              // try to type the password before the real password step, landing it in the username box.
              const pass = [...document.querySelectorAll('#signup-password, input[name="signupPassword"], input[type=password]')].find(visible);
              // Fill one field per tick, and hold the password back ~2s after its field appears:
              // typing it the instant the step switches lands the first characters in the username
              // field, which is still focused. The pause lets the step render and focus move.
              if (pass && !window.__amPassSeen) window.__amPassSeen = Date.now();
              const passReady = pass && (Date.now() - window.__amPassSeen > 2000);
              if (passReady) setInput(pass, data.password);
              else setInput(user, data.username);

              // ----- Gender (optional); click at most once. -----
              if (!window.__amGender) {
                const genderBtn = [...document.querySelectorAll('button, [role=button], label')]
                  .find(b => visible(b) && new RegExp('^\\s*'+data.gender+'\\s*$', 'i').test(b.textContent||''));
                if (genderBtn) { genderBtn.click(); window.__amGender = true; }
              }

              // ----- A CAPTCHA on screen means it's the user's turn: don't touch the submit button. -----
              // Arkose creates a 0x0 / hidden background iframe on initial page load for passive telemetry.
              // We must only pause and wait for the user if the challenge is ACTUALLY visible on screen.
              const isVisibleCaptcha = (el) => {
                if (!el) return false;
                const rect = el.getBoundingClientRect();
                if (rect.width < 100 || rect.height < 100) return false;
                if (rect.bottom < 0 || rect.right < 0 || rect.top > window.innerHeight || rect.left > window.innerWidth) return false;
                const style = window.getComputedStyle(el);
                if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
                return true;
              };
              const captchaCandidates = [
                ...document.querySelectorAll('iframe[src*="arkose"], iframe[src*="funcaptcha"], iframe[title*="erification" i], iframe[title*="challenge" i], #arkose-container, [id*="arkoseFrame"], #fc-iframe-wrap, [id*="funcaptcha"]')
              ];
              const hasVisibleCaptcha = captchaCandidates.some(isVisibleCaptcha);
              if (hasVisibleCaptcha) return 'captcha';

              // ----- Roblox rejected the username (inappropriate / taken / bad characters): new one. -----
              // Only when the field holds the username we meant (not mid-retype), and at most once every
              // few seconds so one error doesn't burn through every attempt before Roblox re-checks.
              if (user && user.value === data.username &&
                  (!window.__amBadAt || Date.now() - window.__amBadAt > 2500)) {
                const txt = (document.body ? document.body.innerText : '').toLowerCase();
                const bad = /username[^]{0,60}(not appropriate|inappropriate|already in use|already taken|is taken|unavailable|not available|isn.?t available|can.?t be used|not allowed|may only contain|can only contain)/.test(txt)
                         || /(not appropriate|inappropriate)[^]{0,40}roblox/.test(txt);
                if (bad) { window.__amBadAt = Date.now(); window.__amSubmitSig = ''; window.__amUserOk = 0; return 'bad-username'; }
              }

              const hasPass = !!document.querySelector('input[type=password]');

              // ----- Email/phone verification step: the user must finish it by hand. -----
              if (!user && !hasPass) {
                const vtxt = (document.body ? document.body.innerText : '').toLowerCase();
                const hasCode = !!document.querySelector('input[type=tel],input[type=number],input[autocomplete=one-time-code],input[inputmode=numeric]');
                if (hasCode && /(verification code|verify it|check your (email|inbox)|enter the code|we sent|security code|phone number|confirm your email)/.test(vtxt))
                  return 'verify';
              }

              // ----- Roblox returned a generic error on this step: allow a few more submits. -----
              if (pass && pass.value === data.password) {
                const txt = (document.body ? document.body.innerText : '').toLowerCase();
                if (/please try again|something went wrong/.test(txt) && (window.__amRetry||0) < 3) {
                  window.__amRetry = (window.__amRetry||0) + 1;
                  window.__amSubmitSig = ''; // let the submit below fire again
                }
              }

              // The submit button for THIS step, by text and in priority order — "Add password" beats
              // a leftover "Continue" from the previous step, and we never hit "Create a passkey",
              // Google/Apple sign-in, Back or Cancel.
              const buttons = [...document.querySelectorAll('button, [role=button]')].filter(b =>
                visible(b) && !/passkey|google|apple|\bback\b|cancel|already have/.test((b.textContent||'').toLowerCase()));
              const match = re => buttons.find(b => re.test((b.textContent||'').trim().toLowerCase()));
              const btn = document.querySelector('#signup-button')
                || match(/\b(add|set)\s*password\b/) || match(/\bsign\s*up\b/)
                || match(/\b(finish|done|create account)\b/) || match(/\b(continue|next|submit)\b/);

              // Ready to advance: on the password step once the password is in and settled; otherwise
              // once the username is. (Roblox splits sign-up across steps — birthday/username, then a
              // separate "Add a password" step whose own field we must fill and submit too.)
              const onPass = !!pass;
              const ready = onPass ? settled(pass, '__amPassOk', data.password)
                                   : settled(user, '__amUserOk', data.username);
              const sig = (user?1:0) + '' + (onPass?1:0) + '|' + (btn ? (btn.textContent||'').trim().toLowerCase().slice(0,16) : '');
              if (ready && btn && !btn.disabled && window.__amSubmitSig !== sig) {
                window.__amSubmitSig = sig;
                btn.click();
                return 'submitted';
              }
              if (ready && window.__amSubmitSig === sig) {
                return 'submitted';
              }
              return onPass ? 'filling-pass' : 'filling-user';
            })({{JsonSerializer.Serialize(new {
                username = _input.Username, password = _input.Password,
                monthName = CultureName(_input.Month), day = _input.Day, year = _input.Year,
                gender = _input.Gender })}});
            """;

        string raw;
        try { raw = await Browser.CoreWebView2.ExecuteScriptAsync(script); }
        catch { return; }
        try { raw = JsonSerializer.Deserialize<string>(raw) ?? ""; } catch { /* keep raw */ }

        if (raw == "bad-username")
        {
            if (_usernameTries++ < 15)
            {
                _input = _input with { Username = RandomCredentials.Username() };
                StatusText.Text = $"Roblox wouldn't accept that username — trying another ({_usernameTries})…";
            }
            else
                StatusText.Text = "Couldn't find a username Roblox accepts — edit it in this window.";
            return;
        }

        if (raw == "captcha")
            StatusText.Text = "Solve the puzzle to finish creating the account — it saves automatically.";
        else if (raw == "verify")
            StatusText.Text = "Roblox wants a verification code — enter it here. The account saves once you're signed in.";
        else if (raw == "submitted")
            StatusText.Text = "Submitting sign-up… (solve the puzzle if it appears)";
        else if (raw is "filling-user" or "filling-pass")
            StatusText.Text = "Filling sign-up form…";
    }

    private static string CultureName(int month) =>
        System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);

    private async Task ClearRobloxSessionCookiesAsync()
    {
        var mgr = Browser.CoreWebView2.CookieManager;
        foreach (var domain in new[] { "https://www.roblox.com", "https://roblox.com", "https://web.roblox.com" })
        {
            foreach (var c in await mgr.GetCookiesAsync(domain))
                if (c.Name is RobloxCookie.Name or "RBXSessionTracker")
                    mgr.DeleteCookie(c);
        }
    }

    private static bool IsTrustedRobloxUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsTrustedRobloxUri(uri);

    private static bool IsTrustedRobloxUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host == "roblox.com" || uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "arkoselabs.com" || uri.Host.EndsWith(".arkoselabs.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "funcaptcha.com" || uri.Host.EndsWith(".funcaptcha.com", StringComparison.OrdinalIgnoreCase));

    private static string StripWebViewMarkers(string ua) =>
        Regex.Replace(ua, @"\s+WebView2/\S+", "").Trim();

    /// <summary>Makes the WebAuthn passkey API no-op on Roblox so it never triggers the Windows passkey dialog.</summary>
    internal const string DisablePasskeysScript = """
        (function () {
          try {
            if (!location.hostname.endsWith('roblox.com')) return;
            if (window.PublicKeyCredential) {
              PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable = () => Promise.resolve(false);
              PublicKeyCredential.isConditionalMediationAvailable = () => Promise.resolve(false);
            }
            if (navigator.credentials) {
              const origCreate = navigator.credentials.create ? navigator.credentials.create.bind(navigator.credentials) : null;
              const origGet = navigator.credentials.get ? navigator.credentials.get.bind(navigator.credentials) : null;
              if (origCreate) {
                navigator.credentials.create = function (options) {
                  if (options && options.publicKey) {
                    return Promise.reject(new DOMException('Passkeys disabled', 'NotAllowedError'));
                  }
                  return origCreate(options);
                };
              }
              if (origGet) {
                navigator.credentials.get = function (options) {
                  if (options && options.publicKey && options.mediation === 'conditional') {
                    return Promise.reject(new DOMException('Passkeys disabled', 'NotAllowedError'));
                  }
                  return origGet(options);
                };
              }
            }
          } catch (e) { }
        })();
        """;

    private void Reload_Click(object sender, RoutedEventArgs e) => Browser.CoreWebView2?.Navigate(SignupUrl);
}
