using System.Text.Json;
using System.Text.Json.Serialization;

namespace AltManager.Core;

/// <summary>A saved launch target (game + optional server details) the user can pick by name.</summary>
public sealed class LaunchPreset
{
    public string Name { get; set; } = "";
    public string Game { get; set; } = "";
    public string JobId { get; set; } = "";
    public string PrivateServerAccessCode { get; set; } = "";
    public string PrivateServerLinkCode { get; set; } = "";
}

/// <summary>A saved schedule that launches a group into a game at a set time.</summary>
public sealed class LaunchSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Group { get; set; } = "";      // account group to launch; "" = all accounts
    public string Game { get; set; } = "";        // place ID / game link
    public string Time { get; set; } = "12:00";    // local HH:mm
    public bool Daily { get; set; } = true;        // repeat each day vs one-shot
    public string LastFired { get; set; } = "";    // yyyy-MM-dd of the last fire, to avoid double-firing
}

/// <summary>How long one account has had a game's client open.</summary>
public sealed class AccountPlayTime
{
    public Guid AccountId { get; set; }
    public string Username { get; set; } = "";
    public double Seconds { get; set; }
    public int Sessions { get; set; }
    public DateTimeOffset LastPlayed { get; set; }
}

/// <summary>A game the user has launched, for the game-history view.</summary>
public sealed class GameHistoryEntry
{
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset LastPlayed { get; set; }
    public int LaunchCount { get; set; }
    public List<AccountPlayTime> Plays { get; set; } = new();
}

/// <summary>
/// Non-secret app preferences, stored in plaintext next to the vault. Nothing here is
/// sensitive (no cookies/passwords) — those stay in the encrypted vault.
/// </summary>
public sealed class AppSettings
{
    // Updates
    public bool AutoUpdateEnabled { get; set; } = true;

    // Startup / window
    public bool LaunchOnWindowsStartup { get; set; }
    public bool AlwaysOnTop { get; set; }

    // Account health
    public bool AutoValidateOnStartup { get; set; }
    public int AutoValidateIntervalMinutes { get; set; }   // 0 = off
    /// <summary>Relaunch a client back into its game if it closes unexpectedly (not when you close it).</summary>
    public bool AutoRejoin { get; set; }

    // Privacy
    public bool StreamerMode { get; set; }
    public bool ConfirmBeforeCopyPassword { get; set; } = true;
    public int ClipboardClearSeconds { get; set; } = 20;   // 0 = never clear
    public int AutoLockMinutes { get; set; }               // 0 = off

    // View / layout (remembered between sessions)
    public bool RememberView { get; set; } = true;
    public bool ShowGroups { get; set; } = true;
    public bool ShowLaunch { get; set; } = true;
    public bool IsGridView { get; set; }
    public int CardColumns { get; set; }
    public int CardWidth { get; set; } = 165;
    public bool ManualOrder { get; set; }   // list sorted by drag order instead of favourite/group/name
    public string LastGroup { get; set; } = "";
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public List<string> CustomGroups { get; set; } = new();

    // Browser
    public string CustomUserAgent { get; set; } = "";

    // Theme
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#5865F2";
    public int GlassFrost { get; set; } = 45;   // Liquid Glass opacity 0 (clear) – 100 (frosted)

    // Local automation API (opt-in; binds to 127.0.0.1 only, bearer-token protected)
    public bool LocalApiEnabled { get; set; }
    public int LocalApiPort { get; set; } = 7963;
    public string LocalApiToken { get; set; } = "";

    public List<LaunchPreset> Presets { get; set; } = new();
    public List<GameHistoryEntry> GameHistory { get; set; } = new();
    public List<LaunchSchedule> Schedules { get; set; } = new();

    // ---------------- Persistence ----------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [JsonIgnore]
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "AltManager", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(DefaultPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath), Json) ?? new()
                : new();
        }
        catch { return new(); } // a corrupt settings file should never block startup
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultPath)!);
            File.WriteAllText(DefaultPath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* best-effort: losing prefs is not worth crashing over */ }
    }
}
