using System.Net.Http;
using System.Net.Http.Json;

namespace AltManager.App.Services;

/// <summary>
/// Posts short alerts to a Discord webhook the user pasted in Tools. Opt-in; the URL is only ever sent
/// to Discord. Fire-and-forget: a failure is reported back as text, never thrown.
/// </summary>
public static class WebhookService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static bool IsValidUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase) ||
         u.Host.EndsWith("discordapp.com", StringComparison.OrdinalIgnoreCase)) &&
        u.AbsolutePath.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Sends one message. Returns null on success, or what went wrong.</summary>
    public static async Task<string?> SendAsync(string url, string title, string text, int color = 0x35B779)
    {
        if (!IsValidUrl(url)) return "That isn't a Discord webhook URL.";
        var body = new
        {
            username = "Alt Manager",
            embeds = new[] { new { title, description = text, color, timestamp = DateTimeOffset.UtcNow } }
        };
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var res = await Http.PostAsJsonAsync(url, body);
                if (res.IsSuccessStatusCode) return null;
                if ((int)res.StatusCode == 429 && attempt == 0)
                {
                    await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2));
                    continue;
                }
                return $"Discord answered HTTP {(int)res.StatusCode}.";
            }
            return "Discord kept rate-limiting.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ex.Message; }
    }
}
