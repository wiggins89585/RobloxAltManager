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

/// <summary>Graphics overrides written to Roblox's ClientAppSettings.json (read when a client starts).</summary>
public sealed class GraphicsSettings
{
    public bool Enabled { get; set; }
    public int FpsCap { get; set; }                       // 0 = leave alone
    public string Quality { get; set; } = "Auto";         // Auto, 1..10
    public string AntiAliasing { get; set; } = "Auto";    // Auto, Off, 2x, 4x, 8x
    public string Textures { get; set; } = "Auto";        // Auto, Low, Medium, High
    public string GraphicsApi { get; set; } = "Auto";     // Auto, Direct3D 11, Vulkan, OpenGL
    public bool NoGrass { get; set; }
    public bool GraySky { get; set; }
    public bool NoShadows { get; set; }
}

/// <summary>A global hotkey: an action name and a key combo such as "Ctrl+Alt+M".</summary>
public sealed class HotkeyBinding
{
    public string Action { get; set; } = "";
    public string Keys { get; set; } = "";
}

/// <summary>One account in a saved setup: where it goes and where its window sits.</summary>
public sealed class SetupAccount
{
    public Guid AccountId { get; set; }
    public string Label { get; set; } = "";
    public long PlaceId { get; set; }
    public string? JobId { get; set; }
    public string? LinkCode { get; set; }
    public Models.SavedWindowBounds? Bounds { get; set; }
}

/// <summary>A whole-setup snapshot: accounts, their games and window spots, and the tool switches.</summary>
public sealed class SavedSetup
{
    public string Name { get; set; } = "";
    public List<SetupAccount> Accounts { get; set; } = new();
    public bool MaxAlts { get; set; }
    public bool Borderless { get; set; }
    public bool AntiAfk { get; set; }
    public bool LowResource { get; set; }
    public GraphicsSettings Graphics { get; set; } = new();
}

/// <summary>Settings for the Tools window (performance, windows, automation, Discord).</summary>
public sealed class ToolsSettings
{
    // Performance
    public bool MaxAlts { get; set; }
    public int MaxAltsTrimMinutes { get; set; } = 5;     // trim background clients' memory this often (0 = never)
    public GraphicsSettings Graphics { get; set; } = new();
    public bool AutoRamClean { get; set; }
    public int RamCleanPercent { get; set; } = 85;

    // Windows
    public bool Borderless { get; set; }
    public bool AllowSmallWindows { get; set; } = true;
    public bool RenameWindows { get; set; } = true;
    public int LayoutGap { get; set; } = 4;
    public int MaxClients { get; set; }                   // 0 = no limit
    public bool AutoClosePrevious { get; set; }
    public Dictionary<Guid, Models.SavedWindowBounds> Pinned { get; set; } = new();

    // Crash breaker (auto-rejoin)
    public int RejoinMaxAttempts { get; set; } = 3;
    public int RejoinWindowMinutes { get; set; } = 5;
    public int RejoinCooldownMinutes { get; set; } = 10;

    // Hotkeys
    public List<HotkeyBinding> Hotkeys { get; set; } = DefaultHotkeys();

    public static List<HotkeyBinding> DefaultHotkeys() => new()
    {
        new() { Action = "Show Alt Manager", Keys = "Ctrl+Alt+M" },
        new() { Action = "Arrange windows", Keys = "Ctrl+Alt+A" },
        new() { Action = "Minimize all clients", Keys = "Ctrl+Alt+H" },
        new() { Action = "Launch selected", Keys = "" },
        new() { Action = "Close all clients", Keys = "" },
        new() { Action = "Toggle Max alts", Keys = "" },
        new() { Action = "Toggle anti-AFK", Keys = "" },
        new() { Action = "Record / stop macro", Keys = "Ctrl+Alt+R" },
        new() { Action = "Play / stop macro", Keys = "Ctrl+Alt+P" },
    };

    // Setups
    public List<SavedSetup> Setups { get; set; } = new();
    public string StartupSetup { get; set; } = "";

    // Anti-AFK
    public bool AntiAfk { get; set; }
    public int AntiAfkMinutes { get; set; } = 15;
    public string AntiAfkMode { get; set; } = "Mouse nudge";   // Mouse nudge, Jump

    // Macro
    public int MacroLoops { get; set; } = 1;          // 0 = repeat until stopped
    public double MacroSpeed { get; set; } = 1.0;

    // Discord
    public bool DiscordRpc { get; set; }
    public string DiscordAppId { get; set; } = "";
    public string WebhookUrl { get; set; } = "";
    public bool WebhookOnCrash { get; set; } = true;
    public bool WebhookOnBreaker { get; set; } = true;
    public bool WebhookOnExpired { get; set; } = true;
    public bool WebhookOnLaunch { get; set; }
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
    public bool AutoReloginExpired { get; set; }   // after validating, offer to re-login expired accounts that have a saved password
    public bool OptimizeClient { get; set; }        // low-resource mode: cap FPS + lowest graphics + below-normal priority + EcoQoS
    public int OptimizeFpsCap { get; set; } = 20;    // FPS cap used in low-resource mode (0 = uncapped)
    /// <summary>Run bulk logins and group joins in simultaneous windows instead of one at a time.</summary>
    public bool ParallelBrowserTasks { get; set; }

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
    public string SortMode { get; set; } = "Default";   // Default | Name | Robux (high) | Robux (low) | Newest | Oldest | Status
    public string LastGroup { get; set; } = "";
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public List<string> CustomGroups { get; set; } = new();

    // Browser
    public string CustomUserAgent { get; set; } = "";
    public string WebView2Folder { get; set; } = "";   // optional WebView2 runtime folder (blank = installed runtime)

    // Theme
    public string Theme { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#5865F2";
    public int GlassFrost { get; set; } = 45;   // Liquid Glass opacity 0 (clear) – 100 (frosted)

    // Local automation API (opt-in; binds to 127.0.0.1 only, bearer-token protected)
    public bool LocalApiEnabled { get; set; }
    public int LocalApiPort { get; set; } = 7963;
    public string LocalApiToken { get; set; } = "";

    public ToolsSettings Tools { get; set; } = new();

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
