using System.Text.RegularExpressions;

namespace AltManager.Core.Launch;

/// <summary>
/// Lets users paste whatever they have: a bare Place ID, a game URL, a legacy
/// private-server URL (?privateServerLinkCode=), or a modern share link (?code=…&amp;type=Server).
/// </summary>
public static partial class LaunchTargetParser
{
    public sealed record Parsed(long? PlaceId, string? LinkCode, string? ShareCode);

    public static Parsed Parse(string input)
    {
        input = input.Trim();
        if (long.TryParse(input, out var id)) return new(id, null, null);

        long? placeId = null;
        var g = GamesPath().Match(input);
        if (g.Success) placeId = long.Parse(g.Groups[1].Value);

        string? linkCode = Query(input, "privateServerLinkCode");
        string? shareCode = null;
        if (input.Contains("/share", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Query(input, "type"), "Server", StringComparison.OrdinalIgnoreCase))
            shareCode = Query(input, "code");

        return new(placeId, linkCode, shareCode);
    }

    private static string? Query(string url, string key)
    {
        var m = Regex.Match(url, $@"[?&]{Regex.Escape(key)}=([^&#]+)", RegexOptions.IgnoreCase);
        return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"/games/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GamesPath();
}
