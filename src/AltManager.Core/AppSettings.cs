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

/// <summary>
/// Non-secret app preferences, stored in plaintext next to the vault. Nothing here is
/// sensitive (no cookies/passwords) — those stay in the encrypted vault.
/// </summary>
public sealed class AppSettings
{
    // Account health
    public bool AutoValidateOnStartup { get; set; }
    public int AutoValidateIntervalMinutes { get; set; }   // 0 = off

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

    // Browser
    public string CustomUserAgent { get; set; } = "";

    public List<LaunchPreset> Presets { get; set; } = new();

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
