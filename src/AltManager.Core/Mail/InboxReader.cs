using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace AltManager.Core.Mail;

/// <summary>A connected IMAP mailbox. The password is encrypted in the vault.</summary>
public sealed class MailInbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Address { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public string EncryptedPassword { get; set; } = "";
}

/// <summary>Reads Roblox verification codes over IMAP. Opens mailboxes read-only and reads only Roblox's mail.</summary>
public static class InboxReader
{
    private static readonly Regex Code = new(@"(?<![\d#])\d{6}(?!\d)", RegexOptions.Compiled);

    /// <summary>Signs in and opens the inbox, throwing with the server's reason if it can't.</summary>
    public static async Task TestAsync(MailInbox inbox, string password, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(inbox, password, ct);
        await client.DisconnectAsync(true, ct);
    }

    /// <summary>Newest unused Roblox code delivered after <paramref name="since"/>, or null.</summary>
    public static async Task<string?> FindCodeAsync(MailInbox inbox, string password, DateTimeOffset since,
                                                    ISet<string> used, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(inbox, password, ct);
        var query = SearchQuery.FromContains("roblox.com").And(SearchQuery.DeliveredAfter(since.UtcDateTime.AddDays(-1)));
        var ids = await client.Inbox.SearchAsync(query, ct);
        foreach (var id in ids.Reverse().Take(15))
        {
            var msg = await client.Inbox.GetMessageAsync(id, ct);
            if (msg.Date < since || !used.Add(msg.MessageId ?? id.ToString())) continue;
            var code = Extract(msg);
            if (code is not null) return code;
            used.Remove(msg.MessageId ?? id.ToString());
        }
        await client.DisconnectAsync(true, ct);
        return null;
    }

    private static string? Extract(MimeMessage msg)
    {
        var text = msg.TextBody ?? Regex.Replace(msg.HtmlBody ?? "", "<(style|script|head)[^>]*>.*?</\\1>|<[^>]+>", " ",
                                                 RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var m = Code.Match(msg.Subject ?? "");
        if (!m.Success) m = Code.Match(text);
        return m.Success ? m.Value : null;
    }

    private static async Task<ImapClient> ConnectAsync(MailInbox inbox, string password, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = 20000 };
        try
        {
            // Proton Mail Bridge runs on this PC with its own self-signed certificate.
            var local = inbox.Host is "127.0.0.1" or "localhost";
            if (local) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(inbox.Host, inbox.Port,
                local ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
            client.AuthenticationMechanisms.Remove("XOAUTH2"); // these are passwords, not OAuth tokens
            await client.AuthenticateAsync(inbox.Address, password, ct);
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
}
