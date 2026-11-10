# Third-party notices

Lockbox itself is MIT licensed (see [LICENSE](LICENSE)). It is built on, and ships with, the work below. Each keeps its own license.

| Component | Used for | License |
|---|---|---|
| [libsodium](https://libsodium.org) | Encryption (Argon2id, XChaCha20-Poly1305, Ed25519) | ISC |
| [NSec](https://nsec.rocks) | .NET bindings for libsodium | MIT |
| [Avalonia UI](https://avaloniaui.net) | Desktop user interface | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | View-model helpers | MIT |
| [Inter](https://rsms.me/inter/) (via Avalonia.Fonts.Inter) | Interface font | SIL Open Font License 1.1 |
| [zxcvbn-core](https://github.com/trichards57/zxcvbn-cs) | Password strength estimates (port of Dropbox's zxcvbn) | MIT |
| [QRCoder](https://github.com/codebude/QRCoder) | Wi-Fi QR codes | MIT |
| [Velopack](https://velopack.io) | Installer and updates | MIT |
| [Microsoft.Win32.SystemEvents](https://github.com/dotnet/runtime) | Locking on sleep and sign-out | MIT |
| [Material Design Icons](https://pictogrammers.com/library/mdi/) | Interface icons | Apache 2.0 |
| [EFF Large Wordlist](https://www.eff.org/dice) | Passphrase generator words | CC BY 3.0 US |
| [Public Suffix List](https://publicsuffix.org) | Telling websites apart for the browser extension and imports | MPL 2.0 |

The EFF Large Wordlist is © Electronic Frontier Foundation, used under the [Creative Commons Attribution 3.0 United States](https://creativecommons.org/licenses/by/3.0/us/) license. The copy in Lockbox is the one shipped by KeePassXC, unmodified.

The Public Suffix List is maintained by Mozilla and its contributors and used under the [Mozilla Public License 2.0](https://mozilla.org/MPL/2.0/). Lockbox embeds a copy (`src/Lockbox.Core/Resources/public_suffix_list.dat`) with the explanatory comments removed to save space; the rules and the license header are unchanged. The original is at [publicsuffix.org/list](https://publicsuffix.org/list/).
