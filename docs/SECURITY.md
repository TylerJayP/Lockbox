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

- Unlocking opens on Home, which shows only counts and status: no names, usernames or sites. The Home button closes the open item again.
- The item list partly hides usernames (`ty•••@example.com`) unless you choose to show them in full. Opening an item shows it in full.
- Auto-lock after inactivity, on Windows lock (Win+L), on sleep and optionally on minimize.
- Copied secrets are cleared from the clipboard after 20 seconds (only if they're still what Lockbox put there), and marked so Windows clipboard history and cloud clipboard skip them.
- The window is excluded from screenshots, recordings and screen sharing (Windows 10 2004+).
- Items marked "protected" need the master password again before they can be viewed, copied or deleted.
- A readable (CSV) export also requires the master password.
- Imported CSV and text files can be overwritten and deleted when the import finishes, since they hold passwords in plain text.
- Only one copy of Lockbox runs per Windows user. Opening it again brings the existing window forward, so two windows can never save different versions of the vault over each other.

## Auto-type

Auto-type (Windows, **off by default**) types a login with simulated keystrokes instead of the clipboard, so no other app can read it from there.

- The Ctrl+Alt+A shortcut is only registered while a vault is unlocked. Locking releases it.
- It picks the login by the active window's title (the browser's name is ignored), and only types when exactly one login matches. Otherwise Lockbox comes forward so you choose.
- It never types into Lockbox itself, and it stops at once if a different window becomes active.
- Protected items ask for the master password before anything is typed.
- Characters are sent as Unicode input, so keyboard layout and Caps Lock can't change them.

## Browser extension

The extension (**off by default**; see [BROWSER.md](BROWSER.md)) never connects to the internet and has no copy of the vault. It asks Lockbox for one login at a time:

```
page ⇄ extension ⇄ browser native messaging (stdin/stdout) ⇄ Lockbox.BrowserHost ⇄ named pipe ⇄ Lockbox
```

- **Who can connect.** The helper is registered (per user, no admin rights) only for Lockbox's extension IDs, so browsers refuse to start it for any other extension. The browser tells the helper which extension started it, and Lockbox rejects every message from an ID it doesn't know. The pipe is created with *current user only* access, so other Windows accounts can't open it.
- **Pairing.** Each browser profile gets a random client ID and must be approved once in Lockbox. Both sides show the same six-digit code, derived from the client ID, so you can tell it's your browser asking. Approved browsers are listed in Settings and can be disconnected; turning the setting off removes them all and the browser registrations.
- **Site matching** uses the Public Suffix List to find each address's registrable domain: `mail.google.com` and `accounts.google.com` are the same site, `github.com.evil.net` is *evil.net*, and different `*.github.io` sites are different sites. IP addresses, `localhost` and single-word hosts must match exactly, and a saved port must match. Only logins for the page's site are offered.
- **What the page can reach.** Only the extension's own pages (the popup and its in-page frames) can ask for logins or passwords. The script inside web pages can only ask about its own frame's address, and gets counts and states, never passwords or usernames. The in-page menu and save bar are extension pages inside a closed shadow root; they're opened with single-use tokens tied to the tab and frame.
- **Filling.** A password is sent only when you pick a login, and is written only into frames whose address is on the login's site. Using a login on a different site needs an explicit confirmation in the popup (Cancel is the default). Protected items need the master password in Lockbox first.
- **Saving** only creates or updates logins for the page's site, keeps the old password in history, and never offers to put back a password that's already in the history. A captured login waits at most two minutes for your answer, in memory only.

Limits: a page holds a password once it's filled into it, and malware in the browser, or another extension that can read every site, can see what's typed or filled. Lockbox can't change that; it's true of every password manager's extension.

## Updates

Installed copies check GitHub Releases for a newer version. Before anything is installed:

1. The app downloads `lockbox-update.json` and `lockbox-update.sig` for that version and verifies the **Ed25519 signature** with the public key built into the app. If it doesn't verify, nothing is downloaded.
2. After downloading, it checks the package's **SHA-256** against the signed manifest. A mismatch deletes the package.
3. Velopack's automatic install-on-startup is turned off, so nothing unverified is ever installed.

The release notes shown under *What's new* come from GitHub and aren't covered by the signature, so Lockbox shows them as formatted text only: links aren't clickable.

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
- **The clipboard is shared.** While a password is on the clipboard (20 s by default), any app can read it. Auto-type avoids the clipboard.
- **Auto-type trusts window titles.** A fake page whose title mentions your bank's name would match your bank login. Only press Ctrl+Alt+A on the real sign-in page (check the address bar). Like typing by hand, keystrokes can be seen by a keylogger already running on the computer.
- **The browser extension extends trust to your browser.** Anything that can read pages in your browser (a malicious extension, malware) can read a password after it's filled. Leave the extension off if you don't use it.
- **Some protections are Windows-only:** clipboard-history exclusion, screen-capture hiding and Win+L locking. Elsewhere, Lockbox detects sleep from gaps in its timer.
- **Attachments live inside the vault file**, so the whole vault (up to 150 MB of attachments) is rewritten on every save. That's fine for documents, but it's not meant for photo libraries.

## Reporting a problem

If you find a security issue, please report it privately: on GitHub, open the repository's **Security** tab and choose **Report a vulnerability**. Please don't open a public issue for it.
