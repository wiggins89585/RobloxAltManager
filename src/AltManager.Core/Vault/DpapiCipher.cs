using System.Security.Cryptography;
using System.Text;

namespace AltManager.Core.Vault;

/// <summary>
/// Windows DPAPI, CurrentUser scope. Zero-config: the key is derived from the
/// Windows logon credentials, so the vault only opens for this Windows user on
/// this machine. Does not protect against malware running as the same user.
/// </summary>
public sealed class DpapiCipher : IVaultCipher
{
    private static readonly byte[] AppEntropy = Encoding.UTF8.GetBytes("AltManager.Vault.v1");

    public string Id => "dpapi";

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context) =>
        ProtectedData.Protect(plaintext.ToArray(), Entropy(context), DataProtectionScope.CurrentUser);

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> context) =>
        ProtectedData.Unprotect(ciphertext.ToArray(), Entropy(context), DataProtectionScope.CurrentUser);

    private static byte[] Entropy(ReadOnlySpan<byte> context)
    {
        var buf = new byte[AppEntropy.Length + context.Length];
        AppEntropy.CopyTo(buf, 0);
        context.CopyTo(buf.AsSpan(AppEntropy.Length));
        return buf;
    }
}
