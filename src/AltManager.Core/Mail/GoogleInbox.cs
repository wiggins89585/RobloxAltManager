using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace AltManager.Core.Mail;

/// <summary>
/// Reads Roblox codes through the Gmail API using Google sign-in, with read-only access and no
/// app password. The user supplies a Desktop OAuth client from their own Google Cloud project.
/// </summary>
public static class GoogleInbox
{
    public const string Host = "gmail-api";
    private const string Scope = "https://www.googleapis.com/auth/gmail.readonly";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Stored in the vault as the inbox's secret.</summary>
    public static string PackSecret(string clientSecret, string refreshToken) => clientSecret + "\n" + refreshToken;

    /// <summary>Runs the browser sign-in and returns the account's address and refresh token.</summary>
    public static async Task<(string Email, string RefreshToken)> SignInAsync(
        string clientId, string clientSecret, Action<Uri> openBrowser, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state = Base64Url(RandomNumberGenerator.GetBytes(16));

            openBrowser(new Uri("https://accounts.google.com/o/oauth2/v2/auth" +
                $"?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                $"&response_type=code&scope={Uri.EscapeDataString(Scope)}&access_type=offline&prompt=consent" +
                $"&code_challenge={challenge}&code_challenge_method=S256&state={state}"));

            var code = await WaitForRedirectAsync(listener, state, ct);
            var tokens = await PostTokenAsync(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["client_secret"] = clientSecret, ["code"] = code,
                ["code_verifier"] = verifier, ["grant_type"] = "authorization_code", ["redirect_uri"] = redirect
            }, ct);
            var refresh = tokens.GetProperty("refresh_token").GetString()!;
            var access = tokens.GetProperty("access_token").GetString()!;
            using var profile = await GetJsonAsync("profile", access, ct);
            return (profile.RootElement.GetProperty("emailAddress").GetString()!, refresh);
        }
        finally { listener.Stop(); }
    }

    /// <summary>Newest unused Roblox code delivered after <paramref name="since"/>, or null.</summary>
    public static async Task<string?> FindCodeAsync(MailInbox inbox, string secret, DateTimeOffset since,
                                                    ISet<string> used, CancellationToken ct = default)
    {
        var parts = secret.Split('\n', 2);
        var tokens = await PostTokenAsync(new Dictionary<string, string>
        {
            ["client_id"] = inbox.ClientId, ["client_secret"] = parts[0],
            ["refresh_token"] = parts[1], ["grant_type"] = "refresh_token"
        }, ct);
        var access = tokens.GetProperty("access_token").GetString()!;

        using var list = await GetJsonAsync("messages?maxResults=15&q=" + Uri.EscapeDataString("from:roblox.com newer_than:1d"), access, ct);
        if (!list.RootElement.TryGetProperty("messages", out var messages)) return null;
        foreach (var m in messages.EnumerateArray())
        {
            var id = m.GetProperty("id").GetString()!;
            if (used.Contains(id)) continue;
            using var full = await GetJsonAsync($"messages/{id}?format=raw", access, ct);
            var raw = full.RootElement.GetProperty("raw").GetString()!.Replace('-', '+').Replace('_', '/');
            raw = raw.PadRight(raw.Length + (4 - raw.Length % 4) % 4, '=');
            using var stream = new MemoryStream(Convert.FromBase64String(raw));
            var msg = await MimeMessage.LoadAsync(stream, ct);
            if (msg.Date < since) continue;
            if (InboxReader.Extract(msg) is { } code) { used.Add(id); return code; }
        }
        return null;
    }

    private static async Task<string> WaitForRedirectAsync(TcpListener listener, string state, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, ct);
            var line = Encoding.ASCII.GetString(buffer, 0, read).Split("\r\n")[0].Split(' ');
            var query = line.Length > 1 && line[1].Contains('?') ? line[1][(line[1].IndexOf('?') + 1)..] : "";
            var args = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

            var isResult = args.ContainsKey("code") || args.ContainsKey("error");
            var body = Encoding.UTF8.GetBytes("<html><body style='font-family:sans-serif'>" +
                (isResult ? "You can close this tab and go back to Alt Manager." : "") + "</body></html>");
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(body, ct);
            if (!isResult) continue; // e.g. the browser asking for a favicon

            if (args.TryGetValue("error", out var error)) throw new InvalidOperationException("Google sign-in failed: " + error);
            if (!args.TryGetValue("state", out var got) || got != state) throw new InvalidOperationException("Google sign-in returned a mismatched response.");
            return args["code"];
        }
    }

    private static async Task<JsonElement> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var res = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException("Google rejected the sign-in: " + text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonDocument> GetJsonAsync(string path, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gmail refused the request ({(int)res.StatusCode}). Is the Gmail API enabled in your project?");
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
