# TermThing

A cross-platform tabbed desktop-focused terminal application (inspired by MobaXTerm) for Windows, Linux, and macOS, built on .NET 10 and Avalonia. All configuration lives in a `config/` folder next to the executable, no installer, no registry entries, no per-user roaming data. Copy the folder anywhere and it works.

> Early development, expect rough edges.

## Features

- **Built-in SFTP browser** -- browse, upload, download, and open remote files without a separate client; transfers run in a background queue with progress overlay
- **Floating tab windows** -- tear any tab out into its own window and drag it back; the underlying PTY keeps running throughout (no reconnect)
- **Integrated text editor** -- open remote or local files in-app with syntax highlighting powered by TextMate grammars
- **Tail / follow any file** -- stream a local or remote file in a dedicated log-tail window with live follow mode
- **Docker monitor panel** -- embedded panel showing running containers with CPU/memory stats; tail or follow container logs without leaving the app
- **Portable JSON config** -- sessions, settings, SSH known-hosts, and UI state are all plain JSON files under `<exe>/config/`; safe to commit, copy, or sync

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Git (for cloning with submodules)

## Build and Run

```powershell
git clone --recurse-submodules https://github.com/tomeko/termthing
cd termthing
dotnet build src/TermThing.sln
dotnet run --project src/TermThing.csproj
```

If you already cloned without `--recurse-submodules`:

```powershell
git submodule update --init --recursive
```

## Configuration

All state is stored in `<exe>/config/`:

| File | Contents |
|---|---|
| `termthing.json` | Saved sessions tree |
| `appsettings.json` | User settings (file associations, recent session count) |
| `tempsettings.json` | Auto-saved UI state (column widths, recent sessions, last session kind) |
| `known_hosts.json` | SSH host-key trust store |

Writes use a `*.tmp` + atomic rename pattern. A corrupted file falls back to defaults silently.

## Acknowledgements

- [Iciclecreek.Avalonia.Terminal](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal) by Tom Laird-McConnell: the terminal control powering every session. Really what makes this tick.
- [SSH.NET](https://github.com/sshnet/SSH.NET): SSH, SFTP, and port-forward support.
- [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) and [AvaloniaEdit.TextMate](https://github.com/AvaloniaUI/AvaloniaEdit): Code editor with TextMate grammar support.
- [TextMateSharp.Grammars](https://github.com/danipen/TextMateSharp): Bundled TextMate grammar definitions.
- [Material.Icons.Avalonia](https://github.com/AvaloniaUtils/Material.Icons.Avalonia): Icons.
- [Cascadia Code](https://github.com/microsoft/cascadia-code) and [Inter](https://rsms.me/inter/): Bundled fonts (SIL Open Font License 1.1).

## License

MIT -- see [LICENSE](LICENSE).

Bundled fonts (Cascadia Code, Inter) are distributed under the [SIL Open Font License 1.1](https://scripts.sil.org/OFL).
