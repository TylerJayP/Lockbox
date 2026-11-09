# Releasing an update

Releases are built and signed on your own computer, so the signing key never goes to GitHub. Installed copies only accept updates signed with that key.

## One-time setup

1. **Install Velopack's packaging tool:**
   ```
   dotnet tool install -g vpk
   ```
2. **Create your signing key.** Choose a strong passphrase; you'll type it for every release.
   ```
   dotnet run --project tools/Lockbox.Release -- keygen
   ```
   This saves the private key, encrypted, to `~/lockbox-release.lbxsign`, and writes the **public** key into `src/Lockbox.App/Services/UpdateTrust.cs`.

   **Back up the `.lbxsign` file** (e.g. on a USB stick). If it's lost, existing installs can't accept new updates, and everyone has to reinstall.
3. **Commit `UpdateTrust.cs`.** It holds only the public key.
4. **Make a GitHub token:**
   - *Settings → Developer settings → Personal access tokens → Fine-grained tokens*
   - Access to this repository only, with **Contents: Read and write**
   - Store it with `setx LOCKBOX_GITHUB_TOKEN "<token>"` (then open a new terminal), or log in with the GitHub CLI (`gh auth login`)

## Each release

```
dotnet run --project tools/Lockbox.Release -- release 0.5.1 --notes notes.md
```

It runs the tests, then:

- builds the Windows app
- packages it with Velopack: installer, full package, and a small delta from the previous release
- signs it
- uploads it to GitHub as a draft, attaches the signature, and publishes

Installed copies offer the update the next time they start.

- **Bump the version every time** (0.5.1 → 0.5.2). Lockbox only offers newer versions.
- `--notes` is an optional Markdown file. Users see it under *What's new* in the update banner.
- `--dry-run` builds and signs everything into `Releases/` without publishing.
- `--skip-tests` skips the test run (not recommended).

## Testing an update end to end

1. Release a version and install it from `Lockbox-win-Setup.exe` on the release page.
2. Release the next version (no code changes needed).
3. Start the installed Lockbox. The *"Lockbox X is available"* banner appears after a few seconds. Click **Update and restart**, then check *Settings → Updates* for the new version number.

## How verification works

See [SECURITY.md → Updates](SECURITY.md#updates).
