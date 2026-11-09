# Releasing an update

Releases are built and signed on your own computer, so the signing key never goes to GitHub. Installed copies only accept updates signed with that key.

## One-time setup

1. **Velopack's packaging tool** (`vpk`) comes with the repo, pinned to the right version in `.config/dotnet-tools.json`. The release tool installs it for you. (If you installed it globally before, you can remove that copy with `dotnet tool uninstall -g vpk`.)
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

1. **Write the release notes** in `release-notes/<version>.md`, e.g. `release-notes/0.6.1.md`. A short list of `-` bullets in plain words reads best. It shows in two places: *What's new* in the app's update banner, and the release page on GitHub (with a short "how to install" footer added).
2. **Commit and push** everything, including the notes.
3. **Release:**
   ```
   dotnet run --project tools/Lockbox.Release -- release 0.6.1
   ```

Before building anything, the tool checks:

- **Your work is committed and pushed.** The release is tagged at your exact commit, so the code on GitHub matches what people install.
- **The version is new and higher** than every existing release. Lockbox only offers newer versions.
- **The Velopack versions match:** the packager and the library inside the app.

Then it runs all the tests and:

- builds the Windows app
- packages it with Velopack: installer, full package, and a small delta from the previous release
- signs it, and checks the signature the same way the app will
- uploads it to GitHub as a draft, attaches the signature and your notes, and publishes

Installed copies offer the update the next time they start. Afterwards, `git fetch --tags` brings the new tag down to your computer.

Options:

- `--notes <file>` uses a different notes file.
- `--dry-run` builds and signs everything into `Releases/` without publishing.
- `--skip-tests` skips the test run (not recommended).
- `--allow-dirty` skips the committed-and-pushed check, for trying the tool out only.

If a release fails halfway (say, the internet drops during the upload), just run the same command again. Unfinished drafts are never visible to anyone, and the tool clears out its own leftover draft first.

## Testing an update end to end

1. Release a version and install it from `Lockbox-win-Setup.exe` on the release page.
2. Release the next version (no code changes needed).
3. Start the installed Lockbox. The *"Lockbox X is available"* banner appears after a few seconds. Click **Update and restart**, then check *Settings → Updates* for the new version number.

## How verification works

See [SECURITY.md → Updates](SECURITY.md#updates).
