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

- **Lockbox.Tests:** unit tests for crypto, the vault file, importers, export, auto-type matching, markdown and update signing.
- **Lockbox.UiTests:** drives the real app (headless, no window appears) through create, unlock, edit, import, export, settings, recovery, auto-type and more, then checks what ended up on disk. Each step is listed in the test output.

GitHub Actions runs them on every push and pull request, on both Linux and Windows ([ci.yml](../.github/workflows/ci.yml)). [CodeQL](../.github/workflows/codeql.yml) scans the code for security problems, and [Dependabot](../.github/dependabot.yml) opens weekly pull requests for library updates.

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
tests/Lockbox.Tests         xUnit tests for the core
tests/Lockbox.UiTests       Headless end-to-end test of the app
tools/Lockbox.Screenshots   Headless renderer for screenshots
tools/Lockbox.Release       keygen + one-command signed release
release-notes/              One Markdown file per version, shown in the update banner
.github/                    CI (Linux + Windows), CodeQL, Dependabot (no release secrets)
```

## Libraries and license

Lockbox is [MIT licensed](../LICENSE). The libraries, font, icons and word list it uses, and their licenses, are listed in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md); both files ship next to `Lockbox.exe`.
