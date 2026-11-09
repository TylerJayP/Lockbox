# Lockbox

A local, offline password manager for Windows, macOS and Linux. Your logins live in one encrypted file on your own computer. There's no server and no account, and your data is never sent anywhere. The only network request is an optional check for app updates on GitHub.

- **Two factors to unlock:** your master password plus a key file (keep it on a USB stick). If the key file is lost, you can type the key from a printed emergency sheet instead.
- **Four item types:** **logins** (username, password, website, and a **"2FA is on" checkmark** with the method), **secure notes**, **payment cards** (number, expiry, security code, PIN, with brand detection and a typo check), and **Wi‑Fi networks** (with a **QR code** guests can scan to join).
- **Custom fields** on any item (account numbers, security questions…). Mark one **Hide** to mask it like a password.
- **Encrypted attachments.** Keep recovery kits, scans or key files inside the vault (up to 20 MB each). Lockbox offers to shred the original after attaching it.
- **Protected items.** Tick "Ask for my master password" on email, banking or cards. Someone at your unlocked computer can't view, copy or delete them without the password.
- **Security check-up** (shield icon). Reused, weak, missing and 2+‑year‑old passwords, plus logins without 2FA, ordered by what to fix first.
- **Hidden from screen sharing.** Discord, OBS, Teams and screenshots see a blank window (Windows 10 2004+; toggle in Settings).
- **Rollback warning.** If an older copy of your vault replaces a newer one (a sync app restoring an old file, or someone swapping it), Lockbox tells you before opening it.
- **Automatic, signed updates.** Installed copies check GitHub on start and offer "Update and restart". An update is installed only if it's signed with the developer's offline key, so a hijacked GitHub account can't push a malicious version. Updates are small deltas (a couple of MB).
- **Keyboard shortcuts:** Ctrl+F search, Enter/Ctrl+C copy password (in the list), Ctrl+B copy username, Ctrl+U open site, Ctrl+N new login, Ctrl+S save, Ctrl+L lock. The full list is in Settings.
- **Backup codes get a second lock.** They're sealed with a separate recovery passphrase, so someone who learns your master password still can't read them. They lock again after 3 minutes.
- **Import your old .txt file.** It understands most hand-written layouts, you review every login before saving, and it can then overwrite and delete the original.
- **Export** (Settings → Export): an **encrypted copy** of the vault for a USB stick, or a **readable CSV in Bitwarden's format** for moving to Bitwarden/KeePassXC/1Password. The CSV asks for your master password first and leaves backup codes out unless you include them.
- **Password history.** When you save a new password, the old one is kept with the date it was replaced (the last 10 per login, adjustable in Settings, 0 = off). You can copy one or put it back with **Use again**, which helps when a site's "change password" page fails halfway. You can also clear one login's history. Old passwords are encrypted with everything else, and they aren't included in CSV exports.
- Password and passphrase **generator**, **strength meter** (zxcvbn), and **weak/reused password** warnings.
- **Auto-lock** after idle time, on Windows lock (Win+L), on sleep, and optionally on minimize. **Copied passwords are wiped** from the clipboard after 20 s and kept out of Windows clipboard history and cloud sync.

## Install

**Windows:** download **`Lockbox-win-Setup.exe`** from the [latest release](https://github.com/TylerJayP/Lockbox/releases/latest) and run it. Lockbox installs for your user only (no admin), adds Start-menu and desktop shortcuts, and **updates itself** from then on.

Windows SmartScreen will say "Windows protected your PC" the first time, because the installer isn't code-signed. Click *More info → Run anyway*. Updates installed by Lockbox itself don't trigger it again.

Your vault and key file live wherever you saved them. Installing, updating or uninstalling Lockbox never touches them.

**From source** (needs the .NET 10 SDK):

```
dotnet run --project src/Lockbox.App
```

**Build a single-file exe without the installer** (it won't update itself):

```
dotnet publish src/Lockbox.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

Use `-r osx-arm64` (Apple Silicon Mac), `-r osx-x64` or `-r linux-x64` for other systems. Mac builds aren't signed, so friends will need to right-click → Open the first time.

**Tests:** `dotnet test` (98 unit tests). The `tools/Lockbox.Screenshots` project renders every screen headlessly (`dotnet run --project tools/Lockbox.Screenshots -- shots`) and drives the real view-models end to end (`… -- smoke`).

## Getting started

1. **Create a vault.** Pick where the `.lbx` file lives (a OneDrive/Google Drive folder is fine, since it's encrypted). Generate a six-word passphrase. Save the key file somewhere else, ideally a USB stick. Save the emergency sheet, **print it**, then delete the file.
2. **Import** your old text file (*Import .txt*). Fix anything with a yellow note, untick anything you don't want, then import. If any logins have backup codes, you'll set your recovery passphrase here.
3. **Delete the original text file.** Lockbox offers to overwrite and delete it. If your Desktop syncs to OneDrive, delete it there too and empty OneDrive's recycle bin. Then change the passwords for your most important accounts (email first), since they sat in plain text.

Each friend runs their own copy with their own vault and key file. There's nothing to share and no server to attack.

## Exporting

- **Save encrypted copy…** writes a verified copy of the vault anywhere you like. It opens with the same master password and key file, so it's safe on a USB stick or a second cloud drive.
- **Export to CSV…** writes every login in [Bitwarden's CSV format](https://bitwarden.com/help/condition-bitwarden-import/) (UTF-8, every cell quoted). In Bitwarden: *Tools → Import data → Bitwarden (csv)*. 2FA status and tags go in the `fields` column, and the first tag becomes the folder. Backup codes are only included if you choose to, and then they go in the notes. Cards and secure notes become Bitwarden notes (card details in `fields`), Wi‑Fi networks become logins, and custom fields go in `fields`. Attachments aren't in the CSV; use *Save copy…* on each one, or the encrypted copy.
  The file is **not encrypted**: import it, then delete it and empty the Recycle Bin. Don't open and re-save it in Excel, which can change passwords that start with `=`, `+`, `-` or `@`.

## Releasing an update

Releases are built and signed on your own computer, so the signing key never goes to GitHub.

**One-time setup**

1. Install Velopack's packaging tool: `dotnet tool install -g vpk`
2. Create your signing key (choose a strong passphrase; you'll type it for each release):
   ```
   dotnet run --project tools/Lockbox.Release -- keygen
   ```
   This saves the private key, encrypted, to `~/lockbox-release.lbxsign`, and writes the **public** key into `src/Lockbox.App/Services/UpdateTrust.cs`. **Back up the `.lbxsign` file** (e.g. on your USB stick). If it's lost, existing installs can't accept new updates, and everyone has to reinstall.
3. Commit `UpdateTrust.cs` (it holds only the public key).
4. Make a GitHub token: *Settings → Developer settings → Fine-grained tokens*, give it access to this repo only, with **Contents: Read and write**. Either set it as `LOCKBOX_GITHUB_TOKEN` or log in with the GitHub CLI (`gh auth login`), and the tool will find it.

**Each release**

```
dotnet run --project tools/Lockbox.Release -- release 0.5.1 --notes notes.md
```

That runs the tests, builds the Windows app, packages it with Velopack (installer, full package and a small delta from the previous release), signs it, uploads it to GitHub as a draft, attaches the signature, and publishes. Installed copies offer the update the next time they start. Add `--dry-run` to build and sign everything locally without publishing.

Bump the version every time (e.g. 0.5.1 → 0.5.2); Lockbox only offers newer versions.

**How the update check works:** the app asks GitHub for the newest release (via Velopack), downloads `lockbox-update.json` + `lockbox-update.sig` for that version, and verifies the Ed25519 signature with the public key built into the app *before downloading the update*. After downloading, it checks the package's SHA-256 against the signed manifest, and deletes the package if anything doesn't match. Velopack's automatic install-on-startup is turned off, so nothing unverified is ever installed.

## How it's protected

```
stretched = Argon2id(password, salt, 256 MiB, N passes)          tuned to about 1 s on the creating PC
vaultKey  = HKDF-SHA256(stretched ‖ keyFile, salt, "Lockbox v1 vault key")
file      = header ‖ XChaCha20-Poly1305(vaultKey, nonce, aad = header, payload)
```

- **Key file** = 32 random bytes from the OS CSPRNG. An attacker needs both it and your password. The password alone is useless.
- **Argon2id** (memory-hard) makes each password guess cost about a second and 256 MiB of RAM, which defeats GPU cracking farms.
- **Authenticated encryption.** Changing any byte of the file (including the header and its KDF settings) makes it refuse to open. A fresh 24-byte nonce is used on every save. The contents are padded to 4 KB blocks so the file size doesn't reveal small changes.
- **Backup codes** are encrypted a second time inside the vault, with their own Argon2id-derived key.
- **Crypto comes from libsodium** (via NSec). Nothing is hand-rolled. Keys live in libsodium-managed memory and are destroyed when the vault locks.
- **Safe saves:** each save writes a temp file, reads it back and decrypts it to prove it's good, then atomically replaces the vault. The previous version is copied to `<vault>.backups/` once per session (the last 20 are kept). If the file changed elsewhere (e.g. a sync from another PC), Lockbox asks instead of silently overwriting.
- **No recovery.** If you lose the master password, or both the key file and the emergency sheet, the data is gone. That's the price of nobody else being able to get in.

### File format (v1)

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
- **.NET strings can't be wiped.** Decrypted passwords shown in the UI live in managed memory until garbage-collected. Malware already running as you could read them. That's true of every password manager while unlocked.
- **The clipboard is shared.** While a password is on the clipboard (20 s), any app can read it. Prefer typing over copying for really sensitive accounts.
- Clipboard-history exclusion, screen-capture hiding and Win+L locking are Windows-only.
- Attachments live inside the vault file, so the whole vault (up to 150 MB of attachments) is rewritten on every save. That's fine for documents, but it's not a place for photo libraries. Elsewhere, Lockbox detects sleep from timer gaps.

## Project layout

```
src/Lockbox.Core     Crypto, file format, vault, importer, generator (no UI)
src/Lockbox.App      Avalonia desktop app (MVVM)
tests/Lockbox.Tests  xUnit tests for the core
tools/Lockbox.Screenshots   Headless renderer + end-to-end smoke test
tools/Lockbox.Release       keygen + one-command signed release (see "Releasing an update")
.github/workflows/ci.yml    Build + tests + smoke checks on every push (no release secrets)
```

## Credits

- [libsodium](https://libsodium.org) via [NSec](https://nsec.rocks) (ISC / MIT)
- [zxcvbn-core](https://github.com/trichards57/zxcvbn-cs) (MIT), a port of Dropbox's zxcvbn
- [Avalonia UI](https://avaloniaui.net) (MIT), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT)
- [Velopack](https://velopack.io) installer and updates (MIT), [QRCoder](https://github.com/codebude/QRCoder) (MIT)
- EFF Large Wordlist for passphrases ([CC BY 3.0 US](https://www.eff.org/dice)), via KeePassXC's copy
- Icons from [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache 2.0)
