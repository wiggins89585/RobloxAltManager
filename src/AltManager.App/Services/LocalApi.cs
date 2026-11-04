namespace AltManager.App.Services;

/// <summary>One account as exposed over the local automation API.</summary>
public sealed record ApiAccount(
    string Id, string Username, long UserId, string Group, string Status, bool Favorite, string Presence);

/// <summary>A launch request from an external client.</summary>
/// <param name="Game">Place ID, game link, or private-server link. Blank = each account's own game / the Launch panel's game.</param>
/// <param name="Accounts">Usernames (or user IDs) to launch. Null/empty + a Group launches that whole group; null/empty + no Group launches the current selection.</param>
/// <param name="Group">Launch every account in this group.</param>
public sealed record ApiLaunchRequest(string? Game, string[]? Accounts, string? Group);

/// <summary>Outcome of a launch request.</summary>
public sealed record ApiLaunchResult(bool Ok, int Count, string Message);

/// <summary>What the local API server can ask of the app. Implemented by the main view-model; all calls are marshalled to the UI thread by the implementation.</summary>
public interface ILocalApiBackend
{
    Task<IReadOnlyList<ApiAccount>> GetAccountsAsync();
    Task<IReadOnlyList<string>> GetGroupsAsync();
    Task<ApiLaunchResult> LaunchAsync(ApiLaunchRequest request);
}
