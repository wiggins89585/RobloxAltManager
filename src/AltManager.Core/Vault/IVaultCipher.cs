namespace AltManager.Core.Vault;

/// <summary>
/// Pluggable cookie cipher. <paramref name="context"/> is bound into the
/// ciphertext (DPAPI entropy / AES-GCM associated data), so a blob copied
/// from one account record onto another fails to decrypt.
/// </summary>
public interface IVaultCipher
{
    string Id { get; }
    byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context);
    byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> context);
}
