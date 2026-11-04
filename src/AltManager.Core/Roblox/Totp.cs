using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AltManager.Core.Roblox;

/// <summary>
/// RFC 6238 time-based one-time passwords (the 6-digit codes an authenticator app shows).
/// Used to auto-answer Roblox's 2-step verification when the account's authenticator seed is stored.
/// </summary>
public static class Totp
{
    public static bool IsValidSecret(string? base32Secret)
    {
        try { return !string.IsNullOrWhiteSpace(base32Secret) && Base32Decode(base32Secret).Length > 0; }
        catch { return false; }
    }

    public static string Generate(string base32Secret, int digits = 6, int periodSeconds = 30, DateTimeOffset? now = null)
    {
        var key = Base32Decode(base32Secret);
        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / periodSeconds;

        var buf = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, counter);
        var hash = HMACSHA1.HashData(key, buf);

        var offset = hash[^1] & 0x0F;
        var bin = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var code = bin % (int)Math.Pow(10, digits);
        return code.ToString().PadLeft(digits, '0');
    }

    private static byte[] Base32Decode(string secret)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        secret = secret.Trim().Replace(" ", "").Replace("-", "").TrimEnd('=').ToUpperInvariant();

        int bits = 0, value = 0;
        var bytes = new List<byte>(secret.Length * 5 / 8 + 1);
        foreach (var c in secret)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) throw new FormatException($"Invalid base32 character '{c}'.");
            value = (value << 5) | idx;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((value >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }
}
