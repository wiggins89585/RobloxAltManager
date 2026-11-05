using AltManager.Core.Mail;
using AltManager.Core.Vault;

namespace AltManager.App.Services;

/// <summary>Finds Roblox email codes across every connected inbox during a login.</summary>
public static class MailCodes
{
    private static readonly HashSet<string> Used = new(); // a code is only handed out once

    public static AccountVault? Vault { get; set; }
    public static bool Available => Vault?.Inboxes.Count > 0;

    /// <summary>Polls the inboxes for a new code, giving up after two minutes.</summary>
    public static async Task<string?> WaitForCodeAsync(DateTimeOffset since)
    {
        if (Vault is not { } vault) return null;
        var end = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < end)
        {
            foreach (var inbox in vault.Inboxes.ToList())
            {
                try
                {
                    var code = await InboxReader.FindCodeAsync(inbox, vault.GetInboxPassword(inbox), since, Used);
                    if (code is not null) return code;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* one bad inbox mustn't stop the others */ }
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        return null;
    }
}
