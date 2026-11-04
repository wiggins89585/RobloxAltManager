namespace AltManager.Core.Models;

public enum AccountStatus
{
    Unknown,
    Valid,
    Expired,
    Error,
    Banned
}

/// <summary>Outcome of checking a cookie against Roblox.</summary>
public enum AccountState
{
    Valid,
    Invalid,   // cookie rejected (401)
    Banned,    // authenticated but moderated (403)
    Error      // network / unexpected
}

/// <summary>
/// Persisted account record. The cookie is NEVER stored in plaintext;
/// <see cref="EncryptedCookie"/> holds the cipher output (base64).
/// </summary>
public sealed class Account
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long UserId { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Group { get; set; } = "Ungrouped";
    public string EncryptedCookie { get; set; } = "";
    /// <summary>Encrypted like the cookie; empty when the password was never seen (cookie imports).</summary>
    public string EncryptedPassword { get; set; } = "";
    /// <summary>Encrypted authenticator (TOTP) seed, for answering 2-step verification. Empty = none.</summary>
    public string EncryptedTotpSecret { get; set; } = "";
    /// <summary>Per-account game ID. Empty = use the Launch panel's game; older saved game links remain readable.</summary>
    public string GameOverride { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    /// <summary>Optional per-account proxy for this app's web-API calls (validation, launch auth, info). Empty = direct.</summary>
    public string Proxy { get; set; } = "";
    /// <summary>Last game-client window position/size for this account; restored on the next launch.</summary>
    public SavedWindowBounds? WindowBounds { get; set; }

    // Cached account info (refreshed on demand), persisted so columns show immediately next launch.
    public long? Robux { get; set; }
    public bool? Premium { get; set; }
    public DateTimeOffset? Created { get; set; }
    public DateTimeOffset? InfoUpdated { get; set; }
    public AccountStatus Status { get; set; } = AccountStatus.Unknown;
    public DateTimeOffset? LastValidated { get; set; }
    public DateTimeOffset Added { get; init; } = DateTimeOffset.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(Alias) ? Username : Alias;
}

/// <summary>Where to send an account when launching.</summary>
public sealed record LaunchTarget(
    long PlaceId,
    string? JobId = null,
    string? PrivateServerAccessCode = null,
    string? PrivateServerLinkCode = null)
{
    public bool IsPrivateServer =>
        !string.IsNullOrWhiteSpace(PrivateServerAccessCode) ||
        !string.IsNullOrWhiteSpace(PrivateServerLinkCode);
}

public sealed record AuthenticatedUser(long Id, string Name, string DisplayName);

/// <summary>A game-client window's position and size in physical pixels.</summary>
public sealed class SavedWindowBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Roblox presence states (matches the API's userPresenceType).</summary>
public enum PresenceType { Offline = 0, Online = 1, InGame = 2, InStudio = 3 }

/// <summary>An account's current Roblox presence.</summary>
public sealed record UserPresence(long UserId, PresenceType Type, string? LastLocation);

/// <summary>A game returned by Roblox search.</summary>
public sealed record GameSearchResult(long PlaceId, string Name, int? Players);

/// <summary>A public game server (job).</summary>
public sealed record ServerInfo(string Id, int Playing, int MaxPlayers, double? Fps, int? Ping);
