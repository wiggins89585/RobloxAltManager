using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AltManager.Core.Models;

namespace AltManager.Core.Vault;

/// <summary>
/// Encrypted on-disk account store. Each cookie is encrypted individually with
/// the account's Guid as cipher context. Writes are atomic (temp file + replace)
/// so a crash mid-save never corrupts the vault.
/// </summary>
public sealed class AccountVault : IDisposable
{
    private const string CheckPlaintext = "altmanager-vault-check";
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly IVaultCipher _cipher;
    private readonly SemaphoreSlim _io = new(1, 1);
    private VaultFile _file;

    public IReadOnlyList<Account> Accounts => _file.Accounts;

    private AccountVault(string path, IVaultCipher cipher, VaultFile file)
    {
        _path = path;
        _cipher = cipher;
        _file = file;
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "AltManager", "vault.json");

    /// <summary>Peek at an existing vault to see which cipher it needs (and the AES salt).</summary>
    public static (string? CipherId, byte[]? Salt) Inspect(string path)
    {
        if (!File.Exists(path)) return (null, null);
        var f = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path), Json);
        return (f?.Cipher, f?.Salt is null ? null : Convert.FromBase64String(f.Salt));
    }

    /// <summary>Open (or create) the vault. Throws <see cref="CryptographicException"/> on a wrong password.</summary>
    public static async Task<AccountVault> OpenAsync(string path, IVaultCipher cipher, byte[]? salt = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VaultFile file;

        if (File.Exists(path))
        {
            await using var s = File.OpenRead(path);
            file = await JsonSerializer.DeserializeAsync<VaultFile>(s, Json)
                   ?? throw new InvalidDataException("Vault file is empty or corrupt.");

            if (file.Cipher != cipher.Id)
                throw new InvalidOperationException($"Vault uses '{file.Cipher}', but '{cipher.Id}' was supplied.");

            // Verify the key before handing back the vault.
            var check = cipher.Decrypt(Convert.FromBase64String(file.Check), "check"u8);
            if (Encoding.UTF8.GetString(check) != CheckPlaintext)
                throw new CryptographicException("Vault key check failed.");
        }
        else
        {
            file = new VaultFile
            {
                Cipher = cipher.Id,
                Salt = salt is null ? null : Convert.ToBase64String(salt),
                Check = Convert.ToBase64String(cipher.Encrypt(Encoding.UTF8.GetBytes(CheckPlaintext), "check"u8))
            };
        }

        var vault = new AccountVault(path, cipher, file);
        await vault.SaveAsync();
        return vault;
    }

    public string GetCookie(Account account)
    {
        var pt = _cipher.Decrypt(Convert.FromBase64String(account.EncryptedCookie), Context(account));
        try { return Encoding.UTF8.GetString(pt); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    public void SetCookie(Account account, string cookie)
    {
        var pt = Encoding.UTF8.GetBytes(cookie);
        try { account.EncryptedCookie = Convert.ToBase64String(_cipher.Encrypt(pt, Context(account))); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    /// <summary>Adds the account, or updates the cookie if the same UserId is already stored.</summary>
    public Account Upsert(AuthenticatedUser user, string cookie)
    {
        var acct = _file.Accounts.FirstOrDefault(a => a.UserId == user.Id);
        if (acct is null)
        {
            acct = new Account { UserId = user.Id };
            _file.Accounts.Add(acct);
        }
        acct.Username = user.Name;
        acct.DisplayName = user.DisplayName;
        acct.Status = AccountStatus.Valid;
        acct.LastValidated = DateTimeOffset.UtcNow;
        SetCookie(acct, cookie);
        return acct;
    }

    public bool Remove(Guid id) => _file.Accounts.RemoveAll(a => a.Id == id) > 0;

    /// <summary>True when the vault is unlocked with a master password (AES), not DPAPI.</summary>
    public bool IsPasswordProtected => _cipher.Id == "aesgcm";

    /// <summary>Checks a typed master password against the vault (for re-unlock). DPAPI vaults return true.</summary>
    public bool VerifyPassword(string password)
    {
        if (!IsPasswordProtected) return true;
        if (_file.Salt is null) return false;
        try
        {
            var salt = Convert.FromBase64String(_file.Salt);
            using var sec = new SecureString();
            foreach (var ch in password) sec.AppendChar(ch);
            sec.MakeReadOnly();

            var cipher = AesGcmCipher.FromPassword(sec, salt);
            try
            {
                var check = cipher.Decrypt(Convert.FromBase64String(_file.Check), "check"u8);
                try { return Encoding.UTF8.GetString(check) == CheckPlaintext; }
                finally { CryptographicOperations.ZeroMemory(check); }
            }
            finally { (cipher as IDisposable)?.Dispose(); }
        }
        catch { return false; }
    }

    public async Task SaveAsync()
    {
        await _io.WaitAsync();
        try
        {
            var tmp = _path + ".tmp";
            await using (var s = File.Create(tmp))
                await JsonSerializer.SerializeAsync(s, _file, Json);

            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, _path);
        }

        finally { _io.Release(); }
    }

    public void Dispose()
    {
        if (_cipher is IDisposable disposable)
            disposable.Dispose();
    }

    private static byte[] Context(Account a) => a.Id.ToByteArray();
    // Separate context so a cookie ciphertext can never be swapped in as a password (and vice versa).
    private static byte[] PasswordContext(Account a) => a.Id.ToByteArray().Concat("pw"u8.ToArray()).ToArray();

    public string? GetPassword(Account account)
    {
        if (string.IsNullOrEmpty(account.EncryptedPassword)) return null;
        var pt = _cipher.Decrypt(Convert.FromBase64String(account.EncryptedPassword), PasswordContext(account));
        try { return Encoding.UTF8.GetString(pt); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    public void SetPassword(Account account, string? password)
    {
        if (string.IsNullOrEmpty(password)) { account.EncryptedPassword = ""; return; }
        var pt = Encoding.UTF8.GetBytes(password);
        try { account.EncryptedPassword = Convert.ToBase64String(_cipher.Encrypt(pt, PasswordContext(account))); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    private static byte[] TotpContext(Account a) => a.Id.ToByteArray().Concat("totp"u8.ToArray()).ToArray();

    public string? GetTotpSecret(Account account)
    {
        if (string.IsNullOrEmpty(account.EncryptedTotpSecret)) return null;
        var pt = _cipher.Decrypt(Convert.FromBase64String(account.EncryptedTotpSecret), TotpContext(account));
        try { return Encoding.UTF8.GetString(pt); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    public void SetTotpSecret(Account account, string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) { account.EncryptedTotpSecret = ""; return; }
        var pt = Encoding.UTF8.GetBytes(secret.Trim());
        try { account.EncryptedTotpSecret = Convert.ToBase64String(_cipher.Encrypt(pt, TotpContext(account))); }
        finally { CryptographicOperations.ZeroMemory(pt); }
    }

    private sealed class VaultFile
    {
        public int Version { get; set; } = 1;
        public string Cipher { get; set; } = "";
        public string? Salt { get; set; }
        public string Check { get; set; } = "";
        public List<Account> Accounts { get; set; } = new();
    }
}
