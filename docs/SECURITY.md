# How Lockbox protects your data

## The vault

```
stretched = Argon2id(password, salt, 256 MiB, N passes)          tuned to about 1 s on the creating PC
vaultKey  = HKDF-SHA256(stretched ‖ keyFile, salt, "Lockbox v1 vault key")
file      = header ‖ XChaCha20-Poly1305(vaultKey, nonce, aad = header, payload)
```

- **Key file** = 32 random bytes from the operating system's secure random generator. An attacker needs both it and your password. The password alone is useless.
- **Argon2id** (memory-hard) makes each password guess cost about a second and 256 MiB of RAM, which defeats GPU cracking farms. The cost is tuned on the computer that creates the vault.
- **Authenticated encryption.** Changing any byte of the file (including the header and its key-derivation settings) makes it refuse to open. A fresh 24-byte nonce is used on every save. Contents are padded to 4 KB blocks so the file size doesn't reveal small changes.
- **Backup codes** are encrypted a second time inside the vault, with their own Argon2id-derived key from a separate recovery passphrase.
- **Crypto comes from libsodium** (via NSec). Nothing is hand-rolled. Keys live in libsodium-managed memory and are destroyed when the vault locks.
- **Unicode-safe passwords.** Passwords are normalized (NFC) before hashing, so the same password typed on any system unlocks the vault.
- **No recovery.** If you lose the master password, or both the key file and the emergency sheet, the data is gone. That's the price of nobody else being able to get in.

## Saving

- Each save writes a temporary file, reads it back and decrypts it to prove it's good, then atomically replaces the vault.
- The previous version is copied to `<vault>.backups/` once per session (the last 20 are kept).
- If the file changed elsewhere since it was opened (e.g. a sync from another PC), Lockbox asks instead of silently overwriting.
- Every save increases a revision number stored *inside* the encrypted vault. Each computer remembers the newest revision it has seen, and warns if an older copy turns up later. That can mean a sync app restored an old file, or someone swapped it.

## While unlocked

- Auto-lock after inactivity, on Windows lock (Win+L), on sleep and optionally on minimize.
- Copied secrets are cleared from the clipboard after 20 seconds (only if they're still what Lockbox put there), and marked so Windows clipboard history and cloud clipboard skip them.
- The window is excluded from screenshots, recordings and screen sharing (Windows 10 2004+).
- Items marked "protected" need the master password again before they can be viewed, copied or deleted.
- A readable (CSV) export also requires the master password.

## Updates

Installed copies check GitHub Releases for a newer version. Before anything is installed:

1. The app downloads `lockbox-update.json` and `lockbox-update.sig` for that version and verifies the **Ed25519 signature** with the public key built into the app. If it doesn't verify, nothing is downloaded.
2. After downloading, it checks the package's **SHA-256** against the signed manifest. A mismatch deletes the package.
3. Velopack's automatic install-on-startup is turned off, so nothing unverified is ever installed.

The signing key lives encrypted on the developer's computer and never goes to GitHub, so a compromised GitHub account can't push a malicious update. See [RELEASING.md](RELEASING.md).

## Vault file format (v1)

| Offset | Size | Field |
|---|---|---|
| 0 | 8 | magic `LBXVAULT` |
| 8 | 2 | format version (1) |
| 10 | 1 | KDF id (1 = Argon2id) |
| 11 | 4 | memory KiB |
| 15 | 4 | passes |
| 19 | 1 | parallelism (1) |
| 20 | 16 | salt |
| 36 | 1 | cipher id (1 = XChaCha20-Poly1305) |
| 37 | 24 | nonce |
| 61 | … | ciphertext + 16-byte tag (payload = u32 length ‖ JSON ‖ zero padding) |

## Honest limitations

- **Not professionally audited.** It uses vetted primitives in a standard construction, but nobody outside has reviewed this code. For your most critical accounts, KeePassXC or Bitwarden have years of scrutiny behind them.
- **.NET strings can't be wiped.** Decrypted passwords shown in the app live in memory until the runtime clears them. Malware already running as you could read them. That's true of every password manager while unlocked.
- **The clipboard is shared.** While a password is on the clipboard (20 s by default), any app can read it. Prefer typing over copying for very sensitive accounts.
- **Some protections are Windows-only:** clipboard-history exclusion, screen-capture hiding and Win+L locking. Elsewhere, Lockbox detects sleep from gaps in its timer.
- **Attachments live inside the vault file**, so the whole vault (up to 150 MB of attachments) is rewritten on every save. That's fine for documents, but it's not meant for photo libraries.

## Reporting a problem

If you find a security issue, please contact the developer privately rather than opening a public issue.
