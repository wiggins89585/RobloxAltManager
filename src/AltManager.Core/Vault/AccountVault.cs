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
    // Accounts the user removed on purpose in this session (so a merge never brings them back).
    private readonly HashSet<Guid> _removed = new();
    // What we last wrote, to notice when another process changed the file underneath us.
    private DateTime _lastWriteUtc;
    private long _lastWriteLength = -1;
    private int _lastSavedCount;
    private DateTime _lastBackupUtc;

    /// <summary>Raised when a save found accounts on disk that this session didn't know about (written by
    /// another running copy or an import) and kept them instead of overwriting them.</summary>
    public event Action<IReadOnlyList<Account>>? AccountsMerged;

    /// <summary>Set when the vault file was unreadable and an older backup was loaded instead.</summary>
    public string? RecoveredFrom { get; private set; }

    private string BackupFolder => Path.Combine(Path.GetDirectoryName(_path)!, "backups");

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
        var f = ReadWithRecovery(path, out _);
        return (f?.Cipher, f?.Salt is null ? null : Convert.FromBase64String(f.Salt));
    }

    /// <summary>Open (or create) the vault. Throws <see cref="CryptographicException"/> on a wrong password.</summary>
    public static async Task<AccountVault> OpenAsync(string path, IVaultCipher cipher, byte[]? salt = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        VaultFile file;

        string? recoveredFrom = null;
        if (File.Exists(path))
        {
            file = ReadWithRecovery(path, out recoveredFrom)
                   ?? throw new InvalidDataException("Vault file is empty or corrupt, and no usable backup was found.");

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

        var vault = new AccountVault(path, cipher, file) { RecoveredFrom = recoveredFrom };
        vault._lastSavedCount = file.Accounts.Count;
        try
        {
            var newest = Directory.Exists(vault.BackupFolder)
                ? new DirectoryInfo(vault.BackupFolder).GetFiles("vault-*.json").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            vault._lastBackupUtc = newest?.LastWriteTimeUtc ?? DateTime.MinValue;
        }
        catch { /* backups are best-effort */ }
        await vault.SaveAsync();
        return vault;
    }

    /// <summary>
    /// Reads the vault file; if it's unreadable (truncated by a crash or power loss), falls back to the
    /// .bak and then the rolling backups, newest first. The unreadable file is kept as *.corrupt-*.
    /// </summary>
    private static VaultFile? ReadWithRecovery(string path, out string? recoveredFrom)
    {
        recoveredFrom = null;
        var primary = TryRead(path);
        if (primary is not null) return primary;

        var candidates = new List<string> { path + ".bak" };
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(path)!, "backups");
            if (Directory.Exists(folder))
                candidates.AddRange(new DirectoryInfo(folder).GetFiles("vault-*.json")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName));
        }
        catch { /* ignore */ }

        foreach (var c in candidates)
        {
            var f = TryRead(c);
            if (f is null) continue;
            try { File.Copy(path, $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
            recoveredFrom = c;
            return f;
        }
        return null;
    }

    private static VaultFile? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var f = JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path), Json);
            return f is { Cipher.Length: > 0, Check.Length: > 0 } ? f : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
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

    public bool Remove(Guid id)
    {
        if (_file.Accounts.RemoveAll(a => a.Id == id) == 0) return false;
        _removed.Add(id); // an explicit removal: a later merge must not resurrect it
        return true;
    }

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
            // 1. Never overwrite accounts this session doesn't know about. If another copy of the app (or
            //    an import) wrote the file since we last did, fold its accounts in instead of erasing them.
            var merged = MergeFromDisk();

            // 2. Keep rolling backups, and always take one right before a save that shrinks the vault.
            BackupIfDue();

            // 3. Serialise on this thread (a snapshot), so the list can't change mid-write, then write
            //    atomically: temp file + replace.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_file, Json);
            var tmp = _path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes);

            if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, _path);

            var info = new FileInfo(_path);
            _lastWriteUtc = info.LastWriteTimeUtc;
            _lastWriteLength = info.Length;
            _lastSavedCount = _file.Accounts.Count;

            if (merged.Count > 0) AccountsMerged?.Invoke(merged);
        }
        finally { _io.Release(); }
    }

    /// <summary>Adds any accounts found on disk that aren't in memory and weren't removed on purpose.</summary>
    private List<Account> MergeFromDisk()
    {
        var added = new List<Account>();
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return added;
            // Unchanged since our own last write: nothing to merge.
            if (_lastWriteLength >= 0 && info.LastWriteTimeUtc == _lastWriteUtc && info.Length == _lastWriteLength) return added;

            var disk = TryRead(_path);
            if (disk is null || disk.Cipher != _file.Cipher || disk.Check != _file.Check) return added; // different key: not ours to merge

            foreach (var a in disk.Accounts)
            {
                if (_removed.Contains(a.Id)) continue;
                if (_file.Accounts.Any(x => x.Id == a.Id || (a.UserId != 0 && x.UserId == a.UserId))) continue;
                _file.Accounts.Add(a);
                added.Add(a);
            }
        }
        catch { /* merging is a safety net; a failure here must not block saving */ }
        return added;
    }

    /// <summary>Copies the current vault file into backups\ at most every few hours, and always before a
    /// save that would leave fewer accounts than the last one. Keeps the newest 30.</summary>
    private void BackupIfDue()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var shrinking = _file.Accounts.Count < _lastSavedCount;
            if (!shrinking && DateTime.UtcNow - _lastBackupUtc < TimeSpan.FromHours(3)) return;
            if (new FileInfo(_path).Length < 64) return; // nothing worth keeping

            Directory.CreateDirectory(BackupFolder);
            File.Copy(_path, Path.Combine(BackupFolder, $"vault-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true);
            _lastBackupUtc = DateTime.UtcNow;

            foreach (var old in new DirectoryInfo(BackupFolder).GetFiles("vault-*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc).Skip(30))
                old.Delete();
        }
        catch { /* backups are best-effort */ }
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
