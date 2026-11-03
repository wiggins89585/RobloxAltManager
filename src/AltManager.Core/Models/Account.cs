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
    /// <summary>Per-account game (place ID / game or server link). Empty = use the Launch panel's game.</summary>
    public string GameOverride { get; set; } = "";
    public string Notes { get; set; } = "";
    public bool Favorite { get; set; }
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
