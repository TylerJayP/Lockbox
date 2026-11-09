namespace Lockbox.Core;

/// <summary>The file isn't a vault, is a newer format, or its header is damaged.</summary>
public sealed class VaultFormatException(string message) : Exception(message);

/// <summary>
/// Decryption failed. With authenticated encryption, a wrong password, a wrong key file and a
/// tampered or corrupted file all look the same, so the message says so.
/// </summary>
public sealed class WrongCredentialsException()
    : Exception("Couldn't open the vault. The master password or key file is wrong, or the vault file was modified or damaged.");

/// <summary>The vault file changed on disk since it was opened (another device, a sync tool, or another copy of the app).</summary>
public sealed class VaultConflictException()
    : Exception("The vault file was changed by something else since you opened it (another device or a sync app).");

/// <summary>The backup-codes section must be unlocked with its passphrase first.</summary>
public sealed class RecoveryLockedException()
    : Exception("Backup codes are sealed. Unlock them with the recovery passphrase first.");
