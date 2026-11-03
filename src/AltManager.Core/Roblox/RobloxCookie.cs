namespace AltManager.Core.Roblox;

public static class RobloxCookie
{
    public const string Name = ".ROBLOSECURITY";

    /// <summary>
    /// Accepts the raw value, "name=value", or a whole pasted Cookie header,
    /// and returns just the .ROBLOSECURITY value (or null if none found).
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().Trim('"');

        foreach (var part in s.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith(Name + "=", StringComparison.Ordinal))
                return part[(Name.Length + 1)..].Trim();
        }

        // Raw value: Roblox cookies start with the "_|WARNING:" banner.
        if (s.StartsWith("_|WARNING:", StringComparison.Ordinal) && !s.Contains(';'))
            return s;

        return null;
    }
}
