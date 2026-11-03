using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security;

namespace AltManager.Core.Vault;

/// <summary>
/// Master-password mode: PBKDF2-SHA256 (600k iterations) → 256-bit key → AES-GCM.
/// Blob layout: nonce(12) | tag(16) | ciphertext. Portable between machines,
/// and protects the vault file even from processes running as the same user
/// (as long as the app is closed / key not in memory).
/// </summary>
public sealed class AesGcmCipher : IVaultCipher, IDisposable
{
    public const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Iterations = 600_000;

    private readonly byte[] _key;

    public string Id => "aesgcm";

    private AesGcmCipher(byte[] key) => _key = key;

    public static AesGcmCipher FromPassword(SecureString password, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(password);
        ArgumentNullException.ThrowIfNull(salt);

        var passwordChars = new char[password.Length];
        var passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(password);
        byte[]? key = null;
        try
        {
            Marshal.Copy(passwordPointer, passwordChars, 0, passwordChars.Length);
            key = Rfc2898DeriveBytes.Pbkdf2(passwordChars.AsSpan(), salt, Iterations, HashAlgorithmName.SHA256, 32);
            return new AesGcmCipher(key);
        }
        catch
        {
            if (key is not null)
                CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(passwordChars.AsSpan()));
            Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
        }
    }

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(SaltSize);

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        var output = new byte[NonceSize + TagSize + plaintext.Length];
        var nonce = output.AsSpan(0, NonceSize);
        var tag = output.AsSpan(NonceSize, TagSize);
        var ct = output.AsSpan(NonceSize + TagSize);

        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ct, tag, context);
        return output;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> blob, ReadOnlySpan<byte> context)
    {
        if (blob.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short.");

        var nonce = blob[..NonceSize];
        var tag = blob.Slice(NonceSize, TagSize);
        var ct = blob[(NonceSize + TagSize)..];
        var pt = new byte[ct.Length];

        using var aes = new AesGcm(_key, TagSize);
        try
        {
            aes.Decrypt(nonce, ct, tag, pt, context); // throws AuthenticationTagMismatchException on tamper/wrong key
            return pt;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(pt);
            throw;
        }
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);
}
