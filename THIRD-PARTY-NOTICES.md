# Third-party notices

The root `LICENSE` is the unmodified MIT license for Usage Overlay, copyright (c) 2026 Haroon. Fork modifications are copyright (c) 2026 danscMax, also under MIT. Original attribution appears in Settings → Acknowledgements and the README.

## Adapted session readers

`src/UsageOverlay/Telemetry` includes adapted readers from [dongyue-19/codex-statusbar](https://github.com/dongyue-19/codex-statusbar), copyright (c) 2026 CodexStatusbar contributors, MIT. Full notice: `licenses/CodexStatusbar.txt` and the source-adjacent license.

## Statusline font and icons

`StatuslineIcons.ttf` is an embedded subset of CaskaydiaCove Nerd Font, not installed as a system font. Component licenses remain applicable; the application's MIT license does not replace font licenses.

| Component | Source | Included notice |
| --- | --- | --- |
| Cascadia Code / CaskaydiaCove | Microsoft Corporation and Saja Typeworks; [Cascadia Code](https://github.com/microsoft/cascadia-code), [Nerd Fonts](https://github.com/ryanoasis/nerd-fonts/tree/master/patched-fonts/CascadiaCode) | `licenses/Cascadia-Code.txt`, `licenses/Statusline-font-metadata.txt` |
| Nerd Fonts modifications | Ryan L McIntyre and contributors; [Nerd Fonts](https://github.com/ryanoasis/nerd-fonts) | `licenses/Nerd-Fonts.txt` |
| Font Awesome glyphs | Fonticons, Inc.; [Font Awesome](https://fontawesome.com); font files under SIL OFL 1.1 | `licenses/Font-Awesome.txt` |
| Octicons glyph | GitHub Inc.; [Octicons](https://github.com/primer/octicons), MIT | `licenses/Octicons.txt` |

Exact texts are in `licenses` beside the executable in both binary packages. Provider names/logos belong to their owners and imply no endorsement.

## Runtime dependencies

Self-contained packages also bundle .NET and NuGet runtime dependencies. Their publisher notices and package metadata are included under `licenses/runtime` in binary packages. `licenses/Microsoft-Data-Sqlite.txt` retains the MIT notice for Microsoft.Data.Sqlite; `licenses/SQLitePCLRaw.txt` and `licenses/SQLitePCLRaw-NOTICE.txt` retain SQLitePCL.raw's Apache 2.0 terms, SourceGear attribution and the native SQLite licensing notice. Project references and restored assets identify component versions.
