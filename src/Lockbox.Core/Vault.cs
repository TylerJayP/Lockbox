using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Lockbox.Core.Crypto;
using Lockbox.Core.Model;
using Lockbox.Core.Storage;
using NSec.Cryptography;

namespace Lockbox.Core;

/// <summary>
/// An unlocked vault. Holds the vault key in libsodium memory until <see cref="Dispose"/> (= lock).
/// Not thread-safe: call it from one thread at a time.
///
/// Entries returned by <see cref="Entries"/> are the live objects: edit a <see cref="Entry.Clone"/>
/// and hand it back through <see cref="UpdateEntry"/>.
/// </summary>
public sealed class Vault : IDisposable
{
    public const string FileExtension = ".lbx";
    public const int BackupsToKeep = 20;
    private const int PadBlock = 4096;
    private static readonly byte[] RecoveryAad = "Lockbox v1 recovery section"u8.ToArray();

    private Key _key;
    private KdfParameters _kdf;
    private byte[] _salt;
    private readonly VaultPayload _payload;
    private byte[]? _fileHash;
    private bool _backedUpThisSession;

    private Key? _recoveryKey;
    private RecoveryPayload? _recovery;
    private bool _disposed;

    // A tiny value sealed with the current key, used to check a re-entered password without touching the file.
    private static readonly byte[] CheckAad = "Lockbox v1 credential check"u8.ToArray();
    private byte[] _checkNonce = [];
    private byte[] _checkToken = [];

    public string FilePath { get; }
    public IReadOnlyList<Entry> Entries => _payload.Entries;
    public VaultSettings Settings => _payload.Settings;
    public KdfParameters KdfParameters => _kdf;
    public bool HasUnsavedChanges { get; private set; }
    public bool IsRecoveryConfigured => _payload.Recovery != null;
    public bool IsRecoveryUnlocked => _recovery != null;

    /// <summary>Stable identity of this vault, used to recognise it on this computer even after it's moved.</summary>
    public Guid VaultId => _payload.VaultId;

    /// <summary>Save counter stored inside the encrypted vault (so it can't be faked without the key).</summary>
    public long Revision => _payload.Revision;
    public DateTimeOffset LastSavedUtc => _payload.LastSavedUtc;

    /// <summary>
    /// After the user knowingly opens an older copy, continue numbering above the newest revision seen,
    /// so this copy becomes the newest and doesn't trigger the warning again.
    /// </summary>
    public void AdoptRevisionAtLeast(long revision)
    {
        ThrowIfDisposed();
        if (_payload.Revision >= revision) return;
        _payload.Revision = revision;
        HasUnsavedChanges = true;
    }

    public const long MaxAttachmentBytes = 20L * 1024 * 1024;
    public const long MaxTotalAttachmentBytes = 150L * 1024 * 1024;
    public long TotalAttachmentBytes => _payload.Attachments.Values.Sum(b => (long)b.Length);

    public string BackupDirectory =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(FilePath))!, Path.GetFileNameWithoutExtension(FilePath) + ".backups");

    private Vault(string path, Key key, KdfParameters kdf, byte[] salt, VaultPayload payload)
    {
        FilePath = Path.GetFullPath(path);
        _key = key;
        _kdf = kdf;
        _salt = salt;
        _payload = payload;
        MakeCheckToken();
    }

    private void MakeCheckToken()
    {
        _checkNonce = VaultCrypto.NewNonce();
        _checkToken = VaultCrypto.Seal(_key, _checkNonce, CheckAad, CheckAad);
    }

    /// <summary>True if this password + key file are the ones currently protecting the vault. Takes ~1 s (runs Argon2id).</summary>
    public bool VerifyCredentials(string masterPassword, ReadOnlySpan<byte> keyFileSecret)
    {
        ThrowIfDisposed();
        using var candidate = VaultCrypto.DeriveVaultKey(masterPassword, keyFileSecret, _salt, _kdf);
        byte[]? ok = VaultCrypto.Open(candidate, _checkNonce, CheckAad, _checkToken);
        if (ok == null) return false;
        CryptographicOperations.ZeroMemory(ok);
        return true;
    }

    // ───────────────────────────── create / open ─────────────────────────────

    public static Vault Create(string path, string masterPassword, ReadOnlySpan<byte> keyFileSecret, KdfParameters kdf)
    {
        if (File.Exists(path)) throw new IOException("A file already exists at that location.");
        if (string.IsNullOrEmpty(masterPassword)) throw new ArgumentException("Master password can't be empty.", nameof(masterPassword));

        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var key = VaultCrypto.DeriveVaultKey(masterPassword, keyFileSecret, salt, kdf);
        var vault = new Vault(path, key, kdf, salt, new VaultPayload { VaultId = Guid.NewGuid() });
        try
        {
            vault.Save();
            return vault;
        }
        catch
        {
            vault.Dispose();
            throw;
        }
    }

    public static Vault Open(string path, string masterPassword, ReadOnlySpan<byte> keyFileSecret)
    {
        byte[] file = File.ReadAllBytes(path);
        var header = VaultHeader.Parse(file);
        var key = VaultCrypto.DeriveVaultKey(masterPassword, keyFileSecret, header.Salt, header.Kdf);

        byte[]? plaintext = VaultCrypto.Open(key, header.Nonce, file.AsSpan(0, VaultHeader.Size), file.AsSpan(VaultHeader.Size));
        if (plaintext == null)
        {
            key.Dispose();
            throw new WrongCredentialsException();
        }

        try
        {
            var payload = Unpack<VaultPayload>(plaintext, PayloadJson.Default.VaultPayload)
                          ?? throw new VaultFormatException("The vault decrypted but its contents are unreadable.");
            payload.Entries ??= [];
            payload.Settings ??= new VaultSettings();
            payload.Attachments ??= [];
            foreach (var e in payload.Entries)
            {
                e.PasswordHistory ??= [];
                e.Fields ??= [];
                e.Attachments ??= [];
                e.Tags ??= [];
            }

            var vault = new Vault(path, key, header.Kdf, header.Salt, payload) { _fileHash = SHA256.HashData(file) };

            // Vaults from before v0.4 have no id yet; give them one (saved with the next change).
            if (payload.VaultId == Guid.Empty) { payload.VaultId = Guid.NewGuid(); vault.HasUnsavedChanges = true; }

            // Drop attachment bytes no item points at any more.
            var referenced = payload.Entries.SelectMany(e => e.Attachments).Select(a => a.Id).ToHashSet();
            foreach (var orphan in payload.Attachments.Keys.Where(id => !referenced.Contains(id)).ToList())
            {
                CryptographicOperations.ZeroMemory(payload.Attachments[orphan]);
                payload.Attachments.Remove(orphan);
                vault.HasUnsavedChanges = true;
            }
            return vault;
        }
        catch
        {
            key.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // ───────────────────────────── entries ─────────────────────────────

    public Entry AddEntry(Entry entry)
    {
        ThrowIfDisposed();
        var e = entry.Clone();
        if (_payload.Entries.Any(x => x.Id == e.Id)) e.Id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        e.CreatedUtc = e.ModifiedUtc = e.PasswordChangedUtc = now;
        e.HasBackupCodes = false;
        e.PasswordHistory = [];
        e.Attachments = [];
        _payload.Entries.Add(e);
        HasUnsavedChanges = true;
        return e;
    }

    public Entry UpdateEntry(Entry edited)
    {
        ThrowIfDisposed();
        int i = _payload.Entries.FindIndex(x => x.Id == edited.Id);
        if (i < 0) throw new KeyNotFoundException("That entry no longer exists.");

        var current = _payload.Entries[i];
        var e = edited.Clone();
        var now = DateTimeOffset.UtcNow;
        bool passwordChanged = e.Password != current.Password;
        e.CreatedUtc = current.CreatedUtc;
        e.HasBackupCodes = current.HasBackupCodes;
        e.PasswordChangedUtc = passwordChanged ? now : current.PasswordChangedUtc;
        e.ModifiedUtc = current.ContentEquals(e) ? current.ModifiedUtc : now;

        // History always comes from the vault's copy, never from the caller (a stale editor can't rewrite it).
        var history = new List<PasswordHistoryItem>(current.PasswordHistory);
        if (passwordChanged && current.Password.Length > 0 && Settings.PasswordHistoryLimit > 0)
        {
            history.RemoveAll(h => h.Password == current.Password);
            history.Insert(0, new PasswordHistoryItem(current.Password, now));
        }
        // A password that's current again isn't "history".
        history.RemoveAll(h => h.Password == e.Password);
        if (history.Count > Settings.PasswordHistoryLimit) history.RemoveRange(Settings.PasswordHistoryLimit, history.Count - Settings.PasswordHistoryLimit);
        e.PasswordHistory = history;
        e.Attachments = [.. current.Attachments];
        _payload.Entries[i] = e;
        HasUnsavedChanges = true;
        return e;
    }

    public void DeleteEntry(Guid id)
    {
        ThrowIfDisposed();
        foreach (var gone in _payload.Entries.Where(x => x.Id == id))
            foreach (var a in gone.Attachments)
                if (_payload.Attachments.Remove(a.Id, out var bytes)) CryptographicOperations.ZeroMemory(bytes);
        _payload.Entries.RemoveAll(x => x.Id == id);
        if (_recovery != null && _recovery.Codes.Remove(id)) Reseal();
        // If the section is sealed, its orphaned codes are pruned the next time it's unlocked.
        HasUnsavedChanges = true;
    }

    // ───────────────────────────── attachments ─────────────────────────────

    /// <summary>Stores a copy of <paramref name="data"/> encrypted inside the vault and links it to an item.</summary>
    public AttachmentRef AddAttachment(Guid entryId, string fileName, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        var entry = _payload.Entries.FirstOrDefault(x => x.Id == entryId) ?? throw new KeyNotFoundException("That item no longer exists.");
        if (data.Length > MaxAttachmentBytes)
            throw new ArgumentException($"Files can be at most {MaxAttachmentBytes / (1024 * 1024)} MB.");
        if (TotalAttachmentBytes + data.Length > MaxTotalAttachmentBytes)
            throw new ArgumentException($"Attachments in one vault can total at most {MaxTotalAttachmentBytes / (1024 * 1024)} MB (the whole vault is rewritten on every save).");

        string name = CleanFileName(fileName);
        if (name.Length == 0) name = "attachment";
        var att = new AttachmentRef(Guid.NewGuid(), name, data.Length, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(data)));
        _payload.Attachments[att.Id] = data.ToArray();
        entry.Attachments.Add(att);
        entry.ModifiedUtc = DateTimeOffset.UtcNow;
        HasUnsavedChanges = true;
        return att;
    }

    /// <summary>Returns a decrypted copy of an attachment's bytes. Zero it when done.</summary>
    public byte[] GetAttachment(Guid attachmentId)
    {
        ThrowIfDisposed();
        if (!_payload.Attachments.TryGetValue(attachmentId, out var bytes)) throw new KeyNotFoundException("That attachment no longer exists.");
        return bytes.ToArray();
    }

    public void RemoveAttachment(Guid entryId, Guid attachmentId)
    {
        ThrowIfDisposed();
        var entry = _payload.Entries.FirstOrDefault(x => x.Id == entryId) ?? throw new KeyNotFoundException("That item no longer exists.");
        if (entry.Attachments.RemoveAll(a => a.Id == attachmentId) == 0) return;
        if (_payload.Attachments.Remove(attachmentId, out var bytes)) CryptographicOperations.ZeroMemory(bytes);
        entry.ModifiedUtc = DateTimeOffset.UtcNow;
        HasUnsavedChanges = true;
    }

    public void RenameAttachment(Guid entryId, Guid attachmentId, string newName)
    {
        ThrowIfDisposed();
        var entry = _payload.Entries.FirstOrDefault(x => x.Id == entryId) ?? throw new KeyNotFoundException("That item no longer exists.");
        int i = entry.Attachments.FindIndex(a => a.Id == attachmentId);
        string name = CleanFileName(newName);
        if (i < 0 || name.Length == 0 || entry.Attachments[i].FileName == name) return;
        entry.Attachments[i] = entry.Attachments[i] with { FileName = name };
        HasUnsavedChanges = true;
    }

    /// <summary>Just the file name (no folders from any OS), without characters Windows forbids.</summary>
    public static string CleanFileName(string? name)
    {
        string n = (name ?? "").Trim();
        int cut = Math.Max(n.LastIndexOf('/'), n.LastIndexOf('\\'));
        if (cut >= 0) n = n[(cut + 1)..];
        var bad = new HashSet<char>("<>:\"|?*" + new string(Enumerable.Range(0, 32).Select(i => (char)i).ToArray()));
        return new string(n.Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
    }

    public void UpdateSettings(Action<VaultSettings> change)
    {
        ThrowIfDisposed();
        change(_payload.Settings);
        _payload.Settings.AutoLockMinutes = Math.Clamp(_payload.Settings.AutoLockMinutes, 1, 240);
        _payload.Settings.ClipboardClearSeconds = Math.Clamp(_payload.Settings.ClipboardClearSeconds, 5, 600);
        _payload.Settings.PasswordHistoryLimit = Math.Clamp(_payload.Settings.PasswordHistoryLimit, 0, 50);

        int limit = _payload.Settings.PasswordHistoryLimit;
        foreach (var e in _payload.Entries.Where(e => e.PasswordHistory.Count > limit))
            e.PasswordHistory.RemoveRange(limit, e.PasswordHistory.Count - limit);
        HasUnsavedChanges = true;
    }

    /// <summary>Forgets every old password stored for one login.</summary>
    public void ClearPasswordHistory(Guid entryId)
    {
        ThrowIfDisposed();
        var e = _payload.Entries.FirstOrDefault(x => x.Id == entryId) ?? throw new KeyNotFoundException("That entry no longer exists.");
        if (e.PasswordHistory.Count == 0) return;
        e.PasswordHistory.Clear();
        HasUnsavedChanges = true;
    }

    // ───────────────────────────── sealed backup codes ─────────────────────────────

    public void SetupRecovery(string passphrase, KdfParameters kdf)
    {
        ThrowIfDisposed();
        if (IsRecoveryConfigured) throw new InvalidOperationException("The backup-codes section is already set up.");
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Passphrase can't be empty.", nameof(passphrase));

        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        _recoveryKey = VaultCrypto.DeriveRecoveryKey(passphrase, salt, kdf);
        _recovery = new RecoveryPayload();
        _payload.Recovery = new SealedRecovery { Kdf = kdf, Salt = salt };
        Reseal();
        HasUnsavedChanges = true;
    }

    /// <summary>Returns false on a wrong passphrase.</summary>
    public bool UnlockRecovery(string passphrase)
    {
        ThrowIfDisposed();
        var sealedSection = _payload.Recovery ?? throw new InvalidOperationException("The backup-codes section hasn't been set up yet.");
        if (IsRecoveryUnlocked) return true;

        var key = VaultCrypto.DeriveRecoveryKey(passphrase, sealedSection.Salt, sealedSection.Kdf);
        byte[]? plaintext = VaultCrypto.Open(key, sealedSection.Nonce, RecoveryAadFor(sealedSection), sealedSection.Ciphertext);
        if (plaintext == null)
        {
            key.Dispose();
            return false;
        }

        try
        {
            _recovery = Unpack<RecoveryPayload>(plaintext, PayloadJson.Default.RecoveryPayload) ?? new RecoveryPayload();
            _recovery.Codes ??= [];
            _recoveryKey = key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        // Drop codes for deleted entries, and make the badges match reality.
        var ids = _payload.Entries.Select(e => e.Id).ToHashSet();
        var orphans = _recovery.Codes.Keys.Where(id => !ids.Contains(id)).ToList();
        foreach (var id in orphans) _recovery.Codes.Remove(id);
        bool pruned = orphans.Count > 0;
        bool badgesChanged = false;
        foreach (var e in _payload.Entries)
        {
            bool has = _recovery.Codes.TryGetValue(e.Id, out var c) && !string.IsNullOrWhiteSpace(c);
            if (e.HasBackupCodes != has) { e.HasBackupCodes = has; badgesChanged = true; }
        }
        if (pruned) Reseal();
        if (pruned || badgesChanged) HasUnsavedChanges = true;
        return true;
    }

    public void LockRecovery()
    {
        _recoveryKey?.Dispose();
        _recoveryKey = null;
        _recovery = null;
    }

    public string GetBackupCodes(Guid entryId)
    {
        ThrowIfDisposed();
        if (_recovery == null) throw new RecoveryLockedException();
        return _recovery.Codes.TryGetValue(entryId, out var codes) ? codes : "";
    }

    public void SetBackupCodes(Guid entryId, string codes)
    {
        ThrowIfDisposed();
        if (_recovery == null) throw new RecoveryLockedException();
        var entry = _payload.Entries.FirstOrDefault(e => e.Id == entryId) ?? throw new KeyNotFoundException("That entry no longer exists.");

        codes = (codes ?? "").Replace("\r\n", "\n").Trim();
        string existing = _recovery.Codes.TryGetValue(entryId, out var c) ? c : "";
        if (codes == existing) return;

        if (codes.Length == 0) _recovery.Codes.Remove(entryId);
        else _recovery.Codes[entryId] = codes;

        entry.HasBackupCodes = codes.Length > 0;
        entry.ModifiedUtc = DateTimeOffset.UtcNow;
        Reseal();
        HasUnsavedChanges = true;
    }

    public void ChangeRecoveryPassphrase(string newPassphrase, KdfParameters kdf)
    {
        ThrowIfDisposed();
        if (_recovery == null) throw new RecoveryLockedException();
        if (string.IsNullOrEmpty(newPassphrase)) throw new ArgumentException("Passphrase can't be empty.", nameof(newPassphrase));

        var salt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var newKey = VaultCrypto.DeriveRecoveryKey(newPassphrase, salt, kdf);
        _recoveryKey?.Dispose();
        _recoveryKey = newKey;
        _payload.Recovery = new SealedRecovery { Kdf = kdf, Salt = salt };
        Reseal();
        HasUnsavedChanges = true;
    }

    private void Reseal()
    {
        if (_recovery == null || _recoveryKey == null || _payload.Recovery == null) return;
        var section = _payload.Recovery;
        byte[] plaintext = Pack(_recovery, PayloadJson.Default.RecoveryPayload, 256);
        try
        {
            section.Nonce = VaultCrypto.NewNonce();
            section.Ciphertext = VaultCrypto.Seal(_recoveryKey, section.Nonce, RecoveryAadFor(section), plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] RecoveryAadFor(SealedRecovery s) =>
        [.. RecoveryAad, .. s.Salt, .. BitConverter.GetBytes(s.Kdf.MemoryKiB), .. BitConverter.GetBytes(s.Kdf.Passes)];

    // ───────────────────────────── master credentials ─────────────────────────────

    /// <summary>
    /// Re-keys the vault with a new master password and/or key file and saves it.
    /// Old backups still open with the OLD credentials; call <see cref="DeleteBackups"/> if those were exposed.
    /// </summary>
    public void ChangeCredentials(string newMasterPassword, ReadOnlySpan<byte> newKeyFileSecret, KdfParameters kdf)
    {
        ThrowIfDisposed();
        if (string.IsNullOrEmpty(newMasterPassword)) throw new ArgumentException("Master password can't be empty.", nameof(newMasterPassword));

        var newSalt = RandomNumberGenerator.GetBytes(Kdf.SaltSize);
        var newKey = VaultCrypto.DeriveVaultKey(newMasterPassword, newKeyFileSecret, newSalt, kdf);

        var (oldKey, oldKdf, oldSalt) = (_key, _kdf, _salt);
        (_key, _kdf, _salt) = (newKey, kdf, newSalt);
        try
        {
            Save();
            oldKey.Dispose();
            MakeCheckToken();
        }
        catch
        {
            (_key, _kdf, _salt) = (oldKey, oldKdf, oldSalt);
            newKey.Dispose();
            throw;
        }
    }

    // ───────────────────────────── saving ─────────────────────────────

    /// <summary>
    /// Encrypts and writes the vault safely:
    /// 1. refuses if the file changed on disk since we read it (unless <paramref name="overwriteExternalChanges"/>),
    /// 2. copies the previous file into the backups folder (once per unlock),
    /// 3. writes a temp file, reads it back and decrypts it to prove it's good,
    /// 4. atomically replaces the vault with it.
    /// </summary>
    public void Save(bool overwriteExternalChanges = false)
    {
        ThrowIfDisposed();

        _payload.Revision++;
        _payload.LastSavedUtc = DateTimeOffset.UtcNow;
        byte[] plaintext = Pack(_payload, PayloadJson.Default.VaultPayload, PadBlock);
        byte[] fileBytes;
        try
        {
            var header = new VaultHeader(_kdf, _salt, VaultCrypto.NewNonce());
            byte[] headerBytes = header.ToBytes();
            byte[] ciphertext = VaultCrypto.Seal(_key, header.Nonce, headerBytes, plaintext);
            fileBytes = [.. headerBytes, .. ciphertext];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        bool exists = File.Exists(FilePath);
        if (exists && _fileHash != null && !overwriteExternalChanges)
        {
            var onDisk = SHA256.HashData(File.ReadAllBytes(FilePath));
            if (!CryptographicOperations.FixedTimeEquals(onDisk, _fileHash)) throw new VaultConflictException();
        }

        if (exists && !_backedUpThisSession)
        {
            BackupCurrentFile();
            _backedUpThisSession = true;
        }

        string dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(fileBytes);
                fs.Flush(flushToDisk: true);
            }

            VerifyWrittenFile(temp);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        _fileHash = SHA256.HashData(fileBytes);
        HasUnsavedChanges = false;
    }

    private void VerifyWrittenFile(string path)
    {
        byte[] written = File.ReadAllBytes(path);
        var header = VaultHeader.Parse(written);
        byte[]? check = VaultCrypto.Open(_key, header.Nonce, written.AsSpan(0, VaultHeader.Size), written.AsSpan(VaultHeader.Size));
        if (check == null) throw new IOException("The vault was written but didn't read back correctly, so it wasn't replaced. Your previous file is untouched.");
        CryptographicOperations.ZeroMemory(check);
    }

    private void BackupCurrentFile()
    {
        Directory.CreateDirectory(BackupDirectory);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string name = Path.GetFileNameWithoutExtension(FilePath);
        string dest = Path.Combine(BackupDirectory, $"{name}_{stamp}{FileExtension}");
        for (int n = 2; File.Exists(dest); n++) dest = Path.Combine(BackupDirectory, $"{name}_{stamp}_{n}{FileExtension}");
        File.Copy(FilePath, dest);

        foreach (var old in new DirectoryInfo(BackupDirectory).GetFiles("*" + FileExtension)
                     .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).Skip(BackupsToKeep))
            TryDelete(old.FullName);
    }

    /// <summary>
    /// Writes an encrypted copy of the vault somewhere else (e.g. a USB stick). Saves pending changes first.
    /// The copy opens with the same master password and key file.
    /// </summary>
    public void SaveEncryptedCopy(string destination)
    {
        ThrowIfDisposed();
        string dest = Path.GetFullPath(destination);
        if (string.Equals(dest, FilePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Choose a different location from the vault itself.");

        if (HasUnsavedChanges) Save();

        string temp = Path.Combine(Path.GetDirectoryName(dest)!, $".{Path.GetFileName(dest)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(FilePath, temp);
            VerifyWrittenFile(temp);
            File.Move(temp, dest, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public IReadOnlyList<FileInfo> ListBackups() =>
        Directory.Exists(BackupDirectory)
            ? new DirectoryInfo(BackupDirectory).GetFiles("*" + FileExtension).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];

    /// <summary>Deletes all backup copies. Returns how many were removed.</summary>
    public int DeleteBackups()
    {
        int n = 0;
        foreach (var f in ListBackups())
            if (TryDelete(f.FullName)) n++;
        return n;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>u32 length || UTF-8 JSON || zero padding to a multiple of <paramref name="block"/> (hides the exact size).</summary>
    private static byte[] Pack<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, int block)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, info);
        try
        {
            int total = 4 + json.Length;
            total = (total + block - 1) / block * block;
            var buf = new byte[total];
            BinaryPrimitives.WriteInt32LittleEndian(buf, json.Length);
            json.CopyTo(buf, 4);
            return buf;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static T? Unpack<T>(byte[] plaintext, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (plaintext.Length < 4) throw new VaultFormatException("The vault contents are truncated.");
        int len = BinaryPrimitives.ReadInt32LittleEndian(plaintext);
        if (len < 0 || len > plaintext.Length - 4) throw new VaultFormatException("The vault contents are damaged.");
        return JsonSerializer.Deserialize(plaintext.AsSpan(4, len), info);
    }

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.Delete(path); return true; } }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Locks the vault: destroys the keys. Unsaved changes are discarded; save first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LockRecovery();
        foreach (var b in _payload.Attachments.Values) CryptographicOperations.ZeroMemory(b);
        _key.Dispose();
    }
}
