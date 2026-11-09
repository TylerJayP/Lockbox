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
dotnet test                                              # unit tests (crypto, vault, importer, export, updates…)
dotnet run --project tools/Lockbox.Screenshots -- smoke  # drives the real app view-models end to end, headless
```

Both run on every push in GitHub Actions ([ci.yml](../.github/workflows/ci.yml)).

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
src/Lockbox.Core            Crypto, file format, vault, importer, export, update signing (no UI)
src/Lockbox.App             Avalonia desktop app (MVVM, CommunityToolkit.Mvvm)
tests/Lockbox.Tests         xUnit tests for the core
tools/Lockbox.Screenshots   Headless renderer and end-to-end smoke test
tools/Lockbox.Release       keygen + one-command signed release
.github/workflows/ci.yml    Build, tests and smoke checks on every push (no release secrets)
```

## Libraries

- [libsodium](https://libsodium.org) via [NSec](https://nsec.rocks) (ISC / MIT)
- [zxcvbn-core](https://github.com/trichards57/zxcvbn-cs) (MIT), a port of Dropbox's zxcvbn
- [Avalonia UI](https://avaloniaui.net) (MIT), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) (MIT)
- [Velopack](https://velopack.io) installer and updates (MIT), [QRCoder](https://github.com/codebude/QRCoder) (MIT)
- EFF Large Wordlist for passphrases ([CC BY 3.0 US](https://www.eff.org/dice)), via KeePassXC's copy
- Icons from [Material Design Icons](https://pictogrammers.com/library/mdi/) (Apache 2.0)
