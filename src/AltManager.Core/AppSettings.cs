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

/// <summary>A game the user has launched, for the game-history view.</summary>
public sealed class GameHistoryEntry
{
    public long PlaceId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset LastPlayed { get; set; }
    public int LaunchCount { get; set; }
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

    // View / layout (remembered between sessions)
    public bool RememberView { get; set; } = true;
    public bool ShowGroups { get; set; } = true;
    public bool ShowLaunch { get; set; } = true;
    public bool IsGridView { get; set; }
    public int CardColumns { get; set; }
    public int CardWidth { get; set; } = 165;
    public string LastGroup { get; set; } = "";
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public List<string> CustomGroups { get; set; } = new();

    // Browser
    public string CustomUserAgent { get; set; } = "";

    public List<LaunchPreset> Presets { get; set; } = new();
    public List<GameHistoryEntry> GameHistory { get; set; } = new();

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
