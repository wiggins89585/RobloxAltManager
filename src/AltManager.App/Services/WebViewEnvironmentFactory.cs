using System.IO;
using Microsoft.Web.WebView2.Core;

namespace AltManager.App.Services;

/// <summary>
/// One shared, persistent WebView2 environment for the login browser.
///
/// Why embedded FunCaptcha (Arkose) often renders blank or loops forever, and what we do about it:
///  1. GPU blocklist / RDP / VMs  → WebGL context fails, Arkose's canvas puzzle never draws.
///     Fix: --ignore-gpu-blocklist, ANGLE on D3D11, and (Chromium 137+) --enable-unsafe-swiftshader
///     so a software WebGL fallback still exists when no GPU is usable.
///  2. Third-party cookie / storage partitioning → the arkoselabs.com iframe loses its session
///     and re-challenges. Fix: tracking prevention set to None for this profile only.
///  3. Ephemeral profile (new temp folder each time) → every login looks like a brand-new
///     device and gets the hardest challenge tier. Fix: persistent user-data folder so
///     cache, IndexedDB and device fingerprints persist between logins.
///  4. UA string that contradicts the real engine (e.g. an old Chrome UA on a new Chromium,
///     while Sec-CH-UA client hints still report the true version) → flagged as automation.
///     Fix: default to the runtime's own UA; custom UA is opt-in (see WebLoginWindow).
///  5. Zero-size / hidden / collapsed WebView → compositor never paints the iframe.
///     Fix: the login window is a normal, visible, resizable window (min 480×640).
/// </summary>
public static class WebViewEnvironmentFactory
{
    private static Task<CoreWebView2Environment>? _env;

    public static string UserDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AltManager", "WebView2");

    /// <summary>Set before the first login if the user is on RDP / a VM without GPU passthrough.</summary>
    public static bool AllowSoftwareWebGL { get; set; } = true;

    /// <summary>Optional folder of a WebView2 runtime (a fixed-version runtime or a Chromium-based Edge),
    /// for machines where the evergreen runtime isn't installed. Blank = use the installed runtime.</summary>
    public static string? BrowserExecutableFolder { get; set; }

    /// <summary>Where to get the free WebView2 runtime (it's separate from the Edge browser).</summary>
    public const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    /// <summary>
    /// Works out which runtime folder to hand WebView2:
    ///  1. the user's custom folder, if set;
    ///  2. the installed runtime via normal discovery (return null = let WebView2 find it);
    ///  3. if discovery is broken (common after removing Edge — the files are there but the
    ///     registration is rejected), the newest runtime version folder found on disk.
    /// </summary>
    private static string? ResolveFolder()
    {
        // Use the custom folder only if it actually hosts a runtime (a wrong level, e.g. the parent
        // of the version folder, silently falls through instead of breaking the browser).
        if (!string.IsNullOrWhiteSpace(BrowserExecutableFolder) && Hosts(BrowserExecutableFolder))
            return BrowserExecutableFolder;
        if (Hosts(null)) return null; // normal discovery works
        return FindInstalledRuntime();

        static bool Hosts(string? folder)
        {
            try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString(folder)); }
            catch { return false; }
        }
    }

    /// <summary>Finds the newest installed WebView2 runtime version folder on disk, or null.</summary>
    private static string? FindInstalledRuntime()
    {
        foreach (var programs in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
                 })
        {
            var root = Path.Combine(programs, "Microsoft", "EdgeWebView", "Application");
            if (programs.EndsWith("Microsoft")) root = Path.Combine(programs, "EdgeWebView", "Application");
            if (!Directory.Exists(root)) continue;
            var best = Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, "msedgewebview2.exe")))
                .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(x => x.ver is not null)
                .OrderByDescending(x => x.ver)
                .Select(x => x.dir)
                .FirstOrDefault();
            if (best is not null) return best;
        }
        return null;
    }

    /// <summary>True when a usable WebView2 runtime is present (installed, found on disk, or the custom folder).</summary>
    public static bool RuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString(ResolveFolder())); }
        catch { return false; }
    }

    /// <summary>Explains that the WebView2 runtime is missing and offers to open the download page.</summary>
    public static void PromptInstallRuntime(System.Windows.Window? owner)
    {
        var answer = System.Windows.MessageBox.Show(owner,
            "The in-app browser (for Login / Create / opening an account) needs the Microsoft Edge " +
            "WebView2 Runtime. That's a free Microsoft component, separate from the Edge browser — removing " +
            "Edge often removes it too. It works alongside any browser you use (Zen, Chrome, etc.).\n\n" +
            "Open the download page now? Install it, then reopen this window.\n\n" +
            "Advanced: if you already have a WebView2 / Chromium-Edge runtime in a custom location, set its " +
            "folder under Settings → Browser.",
            "WebView2 runtime needed", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
        if (answer == System.Windows.MessageBoxResult.Yes)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RuntimeDownloadUrl) { UseShellExecute = true }); }
            catch { /* no shell handler */ }
    }

    // A faulted environment is not cached, so once the runtime is reachable (e.g. after the user sets a
    // folder or installs it) the next attempt succeeds without restarting the app.
    public static Task<CoreWebView2Environment> GetAsync() =>
        _env is { IsFaulted: false, IsCanceled: false } ? _env : _env = CreateAsync(UserDataFolder);

    // Per-account browsing profiles, so each alt's cookies/session stay isolated from the others.
    private static readonly Dictionary<string, Task<CoreWebView2Environment>> _profiles = new();

    /// <summary>A separate persistent profile keyed by account, for "open browser as this account".
    /// An optional <paramref name="proxy"/> routes this profile's traffic through a proxy.</summary>
    public static Task<CoreWebView2Environment> GetForProfileAsync(string key, string? proxy = null)
    {
        var safe = string.Concat(key.Split(Path.GetInvalidFileNameChars()));
        lock (_profiles)
        {
            // Don't reuse a faulted environment, so a retry re-creates it.
            if (!_profiles.TryGetValue(safe, out var env) || env.IsFaulted || env.IsCanceled)
                _profiles[safe] = env = CreateAsync(Path.Combine(UserDataFolder, "profiles", safe), proxy);
            return env;
        }
    }

    /// <summary>Parses this app's proxy string (host:port, host:port:user:pass, or scheme://[user:pass@]host:port)
    /// into a --proxy-server value plus optional credentials. Null when blank/invalid.</summary>
    public static (string Server, string? User, string? Pass)? ParseProxy(string? proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy)) return null;
        var s = proxy.Trim();
        var scheme = "";
        var schemeEnd = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0) { scheme = s[..(schemeEnd + 3)]; s = s[(schemeEnd + 3)..]; }

        string? user = null, pass = null;
        var at = s.LastIndexOf('@');
        if (at >= 0)
        {
            var creds = s[..at];
            s = s[(at + 1)..];
            var ci = creds.IndexOf(':');
            if (ci >= 0) { user = creds[..ci]; pass = creds[(ci + 1)..]; }
        }

        var parts = s.Split(':');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        var server = $"{parts[0]}:{parts[1]}";
        if (parts.Length >= 4) { user = parts[2]; pass = parts[3]; } // host:port:user:pass
        return (scheme + server, user, pass);
    }

    private static Task<CoreWebView2Environment> CreateAsync(string userDataFolder, string? proxy = null)
    {
        Directory.CreateDirectory(userDataFolder);

        var args = new List<string>
        {
            "--ignore-gpu-blocklist",
            "--enable-webgl",
            "--use-angle=d3d11",
            "--enable-gpu-rasterization",
            // Arkose loads in a cross-site iframe; keep its storage un-partitioned.
            "--disable-features=ThirdPartyStoragePartitioning,BlockThirdPartyCookies"
        };
        if (AllowSoftwareWebGL) args.Add("--enable-unsafe-swiftshader");
        if (ParseProxy(proxy) is { } p) args.Add($"--proxy-server={p.Server}");

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', args),
            Language = "en-US"
        };

        // All windows must use identical options for the same user-data folder.
        return CoreWebView2Environment.CreateAsync(ResolveFolder(), userDataFolder, options);
    }
}
