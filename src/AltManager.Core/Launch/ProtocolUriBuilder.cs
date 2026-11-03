using System.Text;
using AltManager.Core.Models;

namespace AltManager.Core.Launch;

/// <summary>
/// Builds the roblox-player: URI that the website's "Play" button produces.
/// The PlaceLauncher URL picks the join mode:
///   RequestGame         → any public server of PlaceId
///   RequestGameJob      → a specific server (JobId / gameId)
///   RequestPrivateGame  → VIP server via accessCode or linkCode
/// </summary>
public static class ProtocolUriBuilder
{
    private const string PlaceLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    public static string Build(string authTicket, LaunchTarget target, long browserTrackerId, string locale = "en_us")
    {
        var launcherUrl = BuildPlaceLauncherUrl(target, browserTrackerId);
        var launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var sb = new StringBuilder("roblox-player:1");
        sb.Append("+launchmode:play");
        sb.Append("+gameinfo:").Append(authTicket);
        sb.Append("+launchtime:").Append(launchTime);
        sb.Append("+placelauncherurl:").Append(Uri.EscapeDataString(launcherUrl));
        sb.Append("+browsertrackerid:").Append(browserTrackerId);
        sb.Append("+robloxLocale:").Append(locale);
        sb.Append("+gameLocale:").Append(locale);
        sb.Append("+channel:");
        sb.Append("+LaunchExp:InApp");
        return sb.ToString();
    }

    public static string BuildPlaceLauncherUrl(LaunchTarget t, long browserTrackerId)
    {
        var q = new List<string> { $"browserTrackerId={browserTrackerId}", $"placeId={t.PlaceId}", "isPlayTogetherGame=false" };

        string request;
        if (t.IsPrivateServer)
        {
            request = "RequestPrivateGame";
            if (!string.IsNullOrWhiteSpace(t.PrivateServerAccessCode))
                q.Add("accessCode=" + Uri.EscapeDataString(t.PrivateServerAccessCode.Trim()));
            if (!string.IsNullOrWhiteSpace(t.PrivateServerLinkCode))
                q.Add("linkCode=" + Uri.EscapeDataString(t.PrivateServerLinkCode.Trim()));
        }
        else if (!string.IsNullOrWhiteSpace(t.JobId))
        {
            request = "RequestGameJob";
            q.Add("gameId=" + Uri.EscapeDataString(t.JobId.Trim()));
        }
        else
        {
            request = "RequestGame";
        }

        return $"{PlaceLauncher}?request={request}&{string.Join('&', q)}";
    }

    /// <summary>Roblox tracker ids are ~12-13 digit numbers; any random value in range works.</summary>
    public static long NewBrowserTrackerId() => Random.Shared.NextInt64(100_000_000_000, 999_999_999_999);
}
