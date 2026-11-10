# Developing Lockbox

## Build and run

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
dotnet run --project src/Lockbox.App
```

Builds without the release signing key (see [RELEASING.md](RELEASING.md)) have automatic updates turned off.

### A standalone exe (no installer, no auto-update)

```
dotnet publish src/Lockbox.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

Use `-r osx-arm64` (Apple Silicon), `-r osx-x64` or `-r linux-x64` for other systems. Mac builds aren't signed, so the first launch needs right-click → Open.

## Tests

```
dotnet test
```

That runs both test projects:

- **Lockbox.Tests:** unit tests for crypto, the vault file, importers, export, auto-type matching, site matching, markdown and update signing.
- **Lockbox.UiTests:** drives the real app (headless, no window appears) through create, unlock, edit, import, export, settings, recovery, auto-type, the browser bridge (through the real helper) and more, then checks what ended up on disk. Each step is listed in the test output.

GitHub Actions runs them on every push and pull request, on both Linux and Windows, and also packages the browser extension and runs Mozilla's add-on checks on it ([ci.yml](../.github/workflows/ci.yml)). [CodeQL](../.github/workflows/codeql.yml) scans the C# and the extension's JavaScript for security problems, and [Dependabot](../.github/dependabot.yml) opens weekly pull requests for library updates.

### The browser extension in real browsers

The UI tests can also run the extension in real browsers against a running Lockbox, playing the person at the keyboard: connecting, filling from the popup and the field button, refusing a look-alike site, generating a password on a sign-up page, saving, updating, two-page sign-ins, protected items and locking. Websites are faked locally, so nothing goes online. It's off by default because it needs the browsers:

```
LOCKBOX_BROWSER_E2E=chrome  dotnet test tests/Lockbox.UiTests    # or firefox, or all
```

- **Chrome** uses [`BrowserE2E/e2e.py`](../tests/Lockbox.UiTests/BrowserE2E/e2e.py): Python with Playwright (`pip install playwright`) and a Chrome or Chromium that can load extensions, such as [Chrome for Testing](https://googlechromelabs.github.io/chrome-for-testing/) (`google-chrome` or `chromium` on the PATH).
- **Firefox** uses [`BrowserE2E/e2e-firefox.mjs`](../tests/Lockbox.UiTests/BrowserE2E/e2e-firefox.mjs): Node with Puppeteer (`npm ci` in that folder) and Firefox 142 or newer (`firefox` on the PATH, or set `LOCKBOX_FIREFOX` to its path). It tests the add-on exactly as it ships, packaged by the release tool.

Set `LOCKBOX_BROWSER_SHOTS` to a folder to get screenshots of the popup, the field menu and the save bar, and `LOCKBOX_BROWSER_E2E_LOG` to a file to get the browser scripts' full output. The scripts are written for Linux: on Windows, browsers find the helper through the registry rather than the folders the scripts set up.

To work on the extension by hand, turn it on in Lockbox's Settings and use *Load unpacked* on the `extension` folder (it keeps the development key, so Lockbox trusts its ID). After changing a file, click the reload arrow on the extension's card.

## Dependencies

Every NuGet version lives in [Directory.Packages.props](../Directory.Packages.props), and shared build settings (target framework, version, license) in [Directory.Build.props](../Directory.Build.props). Project files only list package names.

The Velopack packager (`vpk`) is pinned in [.config/dotnet-tools.json](../.config/dotnet-tools.json) and must match the `Velopack` package version. Dependabot updates the two together, and the release tool refuses to run if they differ. `dotnet tool restore` installs it.

## Screenshots

```
dotnet run --project tools/Lockbox.Screenshots -- shots            # every screen, for checking layouts
dotnet run --project tools/Lockbox.Screenshots -- readme raw-shots # the README images (made-up sample data)
```

The README images in `docs/images` are the `readme` output with a window frame added:

```
python tools/Lockbox.Screenshots/frame_readme_images.py raw-shots docs/images
```

## Project layout

```
src/Lockbox.Core            Crypto, file format, vault, importers, export, auto-type matching, update signing (no UI)
src/Lockbox.App             Avalonia desktop app (MVVM, CommunityToolkit.Mvvm)
src/Lockbox.BrowserHost     Small helper browsers start for the extension; relays to the app over a named pipe
extension/                  The browser extension (Manifest V3, plain JavaScript, no build step)
tests/Lockbox.Tests         xUnit tests for the core
tests/Lockbox.UiTests       Headless end-to-end test of the app (and, when asked, of the extension in Chrome and Firefox)
tools/Lockbox.Screenshots   Headless renderer for screenshots
tools/Lockbox.Release       keygen, one-command signed release, and extension packaging
release-notes/              One Markdown file per version, shown in the update banner
.github/                    CI (Linux + Windows), CodeQL, Dependabot (no release secrets)
```

## Libraries and license

Lockbox is [MIT licensed](../LICENSE). The libraries, font, icons and word list it uses, and their licenses, are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md); both files ship next to `Lockbox.exe`.
