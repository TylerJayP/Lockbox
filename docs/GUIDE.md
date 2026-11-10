# Using Lockbox

## First-time setup

1. **Create a vault.** Choose where the `.lbx` file lives. A OneDrive or Google Drive folder is fine, since the file is encrypted.
2. **Pick a master password.** Click *Generate a passphrase for me* for six random words. It's strong and easy to remember. Lockbox can't reset it, so write it down and keep it somewhere safe.
3. **Save the key file** somewhere other than the vault, ideally a USB stick. You need it plus your password to unlock.
4. **Save the emergency sheet, print it**, then delete the file. If you ever lose the key file, choose *"I don't have my key file"* on the unlock screen and type the key from the sheet.

## Home

Unlocking opens on **Home**: how many logins, notes, cards and Wi‑Fi networks you have, whether any need attention, when the vault was last saved and when it'll lock itself. It shows no names, usernames or sites, so it's safe to have on screen. Click a count to filter the list, or pick an item to open it.

Someone walking up? Click the **Home** button (top of the list) to close whatever's open, or press **Ctrl+L** to lock. In the list, usernames are partly hidden (`ty•••@example.com`) so they can't be read at a glance. You can show them in full in *Settings → Privacy on this computer*.

## Moving from a browser or another password manager

1. **Export your passwords as a CSV file** from the app you're leaving:

   | From | Where to find the export |
   |---|---|
   | Chrome, Brave | Google Password Manager (`chrome://password-manager/settings`) → **Export passwords** |
   | Edge | Settings → Passwords (`edge://settings/passwords`) → **⋯** → **Export passwords** |
   | Firefox | `about:logins` → **⋯** → **Export Passwords** |
   | Safari / Apple Passwords | Passwords app → **File** → **Export All Passwords to File** |
   | Bitwarden | **Tools** → **Export vault** → file format *.csv* |
   | 1Password | **File** → **Export** → choose the account → *CSV* |
   | LastPass | **Account options** → **Advanced** → **Export** |
   | KeePassXC | **Database** → **Export** → **CSV file** |
   | Dashlane, Proton Pass | **Settings** → **Export** → *CSV* |

   Menus move around between versions. If one isn't where the table says, search that app's help for "export passwords".
2. In Lockbox, click **Import**, then **Open .csv or .txt file…**, and choose the export. Click **Find logins**. Lockbox recognizes the format by itself.
3. Review the list. Logins you already have and rows the export repeats start unticked. Click **Import selected**.
4. Let Lockbox **overwrite and delete the CSV**. It holds every password in plain text.
5. Once you're happy with Lockbox, delete the passwords from your browser's own password manager too, so they're only in one place.

What comes across:

- Logins, notes, tags and folders (folders become tags). From LastPass, payment cards become Lockbox cards.
- A Lockbox CSV export brings back everything: cards, Wi‑Fi networks, custom fields, tags, 2FA settings and backup codes.
- **Authenticator keys** (TOTP secrets) from Bitwarden, 1Password, KeePassXC and others are kept in a hidden custom field called *Authenticator key (TOTP)*, and the login is marked "2FA is on". Lockbox doesn't generate the six-digit codes, so keep using your authenticator app.
- Attachments and passkeys aren't part of CSV exports, so they don't come across.

## Moving from a text file

1. Click **Import** and open your old file, or paste its contents.
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

## Browser extension

The Lockbox extension for Chrome, Edge, Brave and Firefox fills logins from a lock button in sign-in boxes or from the toolbar, offers to save new and changed passwords, and generates passwords on sign-up pages. Turn it on in *Settings → Browser extension*, add the extension to your browser and click **Connect**; Lockbox asks you to allow it, with a matching code.

It only ever talks to Lockbox on your own computer, only offers a site's own logins (look-alike sites get nothing), and asks before saving anything. [BROWSER.md](BROWSER.md) has the full setup and how it works.

## Auto-type (Windows)

Auto-type types a login for you, so the password never touches the clipboard. Turn it on in *Settings → Auto-type*.

- **With the shortcut:** click into the sign-in form, press **Ctrl+Alt+A**, and Lockbox types the username, Tab, the password and Enter. It picks the login whose website or name appears in the window's title (for example "GitHub" in *Sign in to GitHub — Mozilla Firefox*).
- **With the button:** if nothing matches, or several logins do, Lockbox comes forward so you can pick. Select the login and click **Auto-type** at the top of it. It types into the window you were in just before.

Safety rules it always follows:

- It only works while Lockbox is unlocked. Locking stops the shortcut.
- It never types into Lockbox itself.
- It stops the moment a different window becomes active, so text can't land in the wrong place.
- Protected items ask for your master password first.

**Sign-ins with two pages** (username first, then password, like Google or Microsoft): add a custom field named `Auto-type` to that login with what to type, for example:

```
{USERNAME}{ENTER}{DELAY 1500}{PASSWORD}{ENTER}
```

You can use `{USERNAME}`, `{PASSWORD}`, `{TAB}`, `{ENTER}` and `{DELAY milliseconds}`. Anything else is typed as written.

If another app already uses Ctrl+Alt+A, Settings says so, and the Auto-type button still works.

## More than one vault

Lockbox remembers the last few vaults you opened on this computer. When there's more than one, the unlock screen shows them as **Recent vaults**: click one to fill in its file (and its key file, if you ticked *Remember where my key file is*). Right-click one to remove it from the list. The list holds file locations only, never passwords or keys.

Lockbox only runs once at a time. Opening it again just brings the open window forward.

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
| Ctrl+Alt+A (anywhere) | Auto-type into the active window, if turned on |
| Ctrl+Shift+L (in your browser) | Fill the login for this site with the Lockbox extension |

## Settings worth knowing

- **Auto-lock** after a few minutes of inactivity (default 5). Lockbox also locks when Windows locks or the computer sleeps.
- **Clear copied passwords** after 20 seconds (adjustable).
- **Hide from screenshots and screen sharing:** on by default.
- **Show full usernames in the item list:** off by default, so the list shows `ty•••@example.com`.
- **Browser extension:** off until you turn it on. The browsers you've connected are listed there, each with a **Disconnect** button.
- **Auto-type** with Ctrl+Alt+A: off until you turn it on.
- **Check for updates** when Lockbox starts: on by default, and the only time Lockbox goes online.
- **Change master password**, optionally with a brand-new key file.
