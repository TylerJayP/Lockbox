# Using Lockbox

## First-time setup

1. **Create a vault.** Choose where the `.lbx` file lives. A OneDrive or Google Drive folder is fine, since the file is encrypted.
2. **Pick a master password.** Click *Generate a passphrase for me* for six random words. It's strong and easy to remember. Lockbox can't reset it, so write it down and keep it somewhere safe.
3. **Save the key file** somewhere other than the vault, ideally a USB stick. You need it plus your password to unlock.
4. **Save the emergency sheet, print it**, then delete the file. If you ever lose the key file, choose *"I don't have my key file"* on the unlock screen and type the key from the sheet.

## Moving from a text file

1. Click **Import .txt** and open your old file, or paste its contents.
2. Lockbox understands most layouts, even mixed together:
   ```
   Netflix
   email: me@example.com
   password: hunter2

   Steam | myname | St3amPass
   Amazon - me@example.com - Amaz0n!

   Google
   user: me@gmail.com
   pass: …
   2FA: authenticator
   Backup codes:
   1234 5678
   2345 6789
   ```
3. Review every login. Anything Lockbox had to guess has a yellow note, and logins you already have are unticked. Fix what's needed and click **Import selected**.
4. Let Lockbox **overwrite and delete the original file**. If your Desktop syncs to OneDrive, delete it there too and empty OneDrive's recycle bin.
5. Since those passwords sat in plain text, change the ones for your most important accounts, starting with email.

## Item types

| Type | What it holds |
|---|---|
| **Login** | Username, password, website, 2FA checkmark and method, backup codes |
| **Secure note** | Free text: combinations, instructions, anything private |
| **Payment card** | Name, number, expiry, security code, PIN (brand detection and a typo check) |
| **Wi‑Fi network** | Network name, password, security type, and a QR code guests can scan to join |

Every type can also have **custom fields** (tick *Hide* to mask one like a password), **attachments** (up to 20 MB each, encrypted inside the vault) and **tags**.

## Backup codes

Backup codes skip two-factor login entirely, so Lockbox locks them a second time behind a separate **recovery passphrase**. The first time you add codes, you choose that passphrase. Pick something different from your master password. Unlocked codes lock again after 3 minutes.

## Protected items

Tick **"Ask for my master password before showing this item"** on anything sensitive. It opens covered, and viewing, copying or deleting it needs your master password, even though Lockbox is already unlocked. It covers itself again when you click away.

## Password history

When you save a new password, the old one is kept with the date it was replaced (the last 10 per login, changeable in Settings). Open **Password history** under the password field to copy an old one or click **Use again**. That's handy when a site's "change password" page fails halfway.

## Security check-up

The shield icon at the top of the sidebar lists reused, weak, missing and 2+ year old passwords, plus logins without two-factor, most urgent first. **Fix →** jumps straight to the item.

## Exporting

*Settings → Export*:

- **Save encrypted copy…** writes a verified copy of the vault anywhere you like. It opens with the same master password and key file, so it's safe on a USB stick or a second cloud drive.
- **Export to CSV…** writes everything in [Bitwarden's CSV format](https://bitwarden.com/help/condition-bitwarden-import/). In Bitwarden: *Tools → Import data → Bitwarden (csv)*. It asks for your master password first, and leaves backup codes out unless you include them.
  - Cards and notes become Bitwarden notes; Wi‑Fi networks become logins.
  - Custom fields, tags and 2FA status go in the `fields` column.
  - Attachments aren't included. Use *Save copy…* on each one, or the encrypted copy.
  - The file is **not encrypted**. Import it, then delete it. Don't open and re-save it in Excel, which can change passwords that start with `=`, `+`, `-` or `@`.

## Backups

The first time you save after unlocking, Lockbox copies the previous version of the vault into a `<vault name>.backups` folder next to it, keeping the last 20. Settings shows how many there are and can open or clear the folder.

## Keyboard shortcuts

| Keys | Action |
|---|---|
| Ctrl+F | Search |
| Ctrl+N | New login |
| Enter or Ctrl+C (in the list) | Copy password / card number |
| Ctrl+Shift+C | Copy password from anywhere |
| Ctrl+B | Copy username / network name |
| Ctrl+U | Open website |
| Ctrl+S | Save |
| Delete (in the list) | Delete item |
| Esc (in search) | Clear search and filters |
| Ctrl+L | Lock now |

## Settings worth knowing

- **Auto-lock** after a few minutes of inactivity (default 5). Lockbox also locks when Windows locks or the computer sleeps.
- **Clear copied passwords** after 20 seconds (adjustable).
- **Hide from screenshots and screen sharing:** on by default.
- **Check for updates** when Lockbox starts: on by default, and the only time Lockbox goes online.
- **Change master password**, optionally with a brand-new key file.
