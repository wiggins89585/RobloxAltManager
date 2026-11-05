using System.Security.Cryptography;
using System.Text;

namespace AltManager.App.Services;

/// <summary>Generates random usernames and passwords for new Roblox accounts.</summary>
public static class RandomCredentials
{
    private static int R(int n) => RandomNumberGenerator.GetInt32(n);

    /// <summary>A pronounceable name (letters) plus a few digits, within Roblox's 3–20 char rule.</summary>
    public static string Username()
    {
        const string cons = "bcdfghjklmnprstvwz", vow = "aeiou";
        var sb = new StringBuilder();
        var syllables = 3 + R(2); // 3–4 → 6–8 letters
        for (var i = 0; i < syllables; i++)
        {
            sb.Append(cons[R(cons.Length)]);
            sb.Append(vow[R(vow.Length)]);
        }
        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.Append(100 + R(9900)).ToString(); // + 3–4 digits
    }

    /// <summary>A strong password satisfying Roblox's rules (upper, lower, digit, symbol).</summary>
    public static string Password()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnpqrstuvwxyz",
                     digit = "23456789", sym = "!@#$%*?";
        var all = upper + lower + digit + sym;
        var chars = new List<char>
        {
            upper[R(upper.Length)], lower[R(lower.Length)], digit[R(digit.Length)], sym[R(sym.Length)],
        };
        while (chars.Count < 14) chars.Add(all[R(all.Length)]);
        for (var i = chars.Count - 1; i > 0; i--) // shuffle so the guaranteed classes aren't always first
        {
            var j = R(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }
}
