# TermThing — Agent Notes

Cross-platform tabbed terminal app: Local shells, SSH (with jump-host chains and SFTP),
and Serial. Built with **.NET 10 / Avalonia 11** on top of a **forked**
[`Iciclecreek.Avalonia.Terminal`](Iciclecreek.Avalonia.Terminal/README.md) submodule.

## Build & Run

```powershell
dotnet build src/TermThing.sln
dotnet run --project src/TermThing.csproj
```

- Target: `net10.0`, `WinExe`, nullable enabled, `LangVersion=latest`.
- There is no test project — verify changes by building and running the app.
- The terminal library is consumed via a `<ProjectReference>` to the submodule
  at `Iciclecreek.Avalonia.Terminal/src/Iciclecreek.Avalonia.TerminalWindow/...`.
  Clone with `--recurse-submodules`, or run `git submodule update --init --recursive`.

## Project Layout (under [src/](src/))

| Folder | Purpose |
|---|---|
| [Program.cs](src/Program.cs), [App.axaml.cs](src/App.axaml.cs) | Avalonia entry point (Cascadia Code + Inter fonts, Fluent theme). |
| [Views/](src/Views/) | All windows & dialogs (`*.axaml` + code-behind). `MainWindow` is the orchestrator. |
| [Sessions/](src/Sessions/) | Session model, launcher contracts, registry. One launcher per `SessionKind`. |
| [Sessions/Launchers/](src/Sessions/Launchers/) | `LocalSessionLauncher`, `SshSessionLauncher`, `SerialSessionLauncher`. |
| [Ssh/](src/Ssh/) | SSH.NET-based PTY connector, jump-host chain resolver, `known_hosts` service. |
| [Sftp/](src/Sftp/) | SFTP transfer queue, OS file launcher, "open with" plumbing. |
| [Configuration/](src/Configuration/) | Persisted JSON config, settings, app paths. |
| [Security/](src/Security/) | `ISecretVault` placeholder — **secrets are currently never persisted**. |

## Core Architecture

Adding a new session type means: add a `SessionKind` enum value, a
`SessionSettings`-derived record (with a `[JsonDerivedType]` discriminator on
[SessionDefinition.cs](src/Sessions/SessionDefinition.cs)), an
`ISessionLauncher` implementation, and register it in
[`MainWindow` ctor](src/Views/MainWindow.axaml.cs).

- [`ISessionLauncher`](src/Sessions/ISessionLauncher.cs) — `LaunchAsync(definition, promptHost, ct)` returns an `ISessionInstance`.
- [`ISessionInstance`](src/Sessions/ISessionInstance.cs) — wraps the `Control` placed as tab content + `SessionEnded` event + `Kill()`.
- [`ISessionPromptHost`](src/Sessions/ISessionPromptHost.cs) — implemented by `MainWindow`; launchers call it to prompt for SSH password / key passphrase. **Never store secrets in `SessionDefinition` itself** — use the `[JsonIgnore] Transient*` fields on settings records.
- [`SessionLauncherRegistry`](src/Sessions/SessionLauncherRegistry.cs) — keyed by `SessionKind`, populated once in `MainWindow`.
- [`SessionDefinition`](src/Sessions/SessionDefinition.cs) uses `System.Text.Json` polymorphism (`$type` discriminator: `local`, `ssh`, `serial`).

## Configuration & Persistence

All state is portable JSON under `<exe>/config/` (see
[AppPaths.cs](src/Configuration/AppPaths.cs)):

| File | Contents | Loader |
|---|---|---|
| `termthing.json` | Sessions tree (`AppConfig.RootGroup`) | [ConfigStore](src/Configuration/ConfigStore.cs) |
| `appsettings.json` | User-curated settings (file associations, recent count) | [SettingsService](src/Configuration/SettingsService.cs) |
| `tempsettings.json` | Auto-saved UI state (column widths, recents, last kind) | same |
| `known_hosts.json` | SSH host-key trust store | [KnownHostsService](src/Ssh/KnownHostsService.cs) |

Writes go through a `*.tmp` + `File.Move(overwrite: true)` swap — preserve that
pattern for any new persisted file. Corrupted config silently falls back to defaults.

## Conventions

- **C# style**: file-scoped namespaces, `using` collected at top, primary constructors and target-typed `new()` are fine. XML doc comments on public interfaces / records.
- **Avalonia views**: code-behind `*.axaml.cs` is partial with `InitializeComponent()`; `AvaloniaUseCompiledBindingsByDefault` is **off** (project-wide) — keep classic bindings unless you opt in per-control.
- **UI thread**: marshal session callbacks back via `Dispatcher.UIThread.Post(...)` (see `LocalSessionInstance`).
- **No DI container** — services are constructed in `MainWindow` and passed by reference.
- **Don't kill the terminal on detach**: when re-parenting a `TerminalControl` between windows (e.g. floating tab → main), call `BeginReparent()` / `EndReparent()` — that's the whole reason for the fork. See [doc/InternalDocs.md](doc/InternalDocs.md).

## Working with the Vendored Terminal Fork

The submodule sits at [`Iciclecreek.Avalonia.Terminal/`](Iciclecreek.Avalonia.Terminal/)
on branch `reparent-support` of `tomeko/Iciclecreek.Avalonia.Terminal`. Detailed
fork/PR/sync workflow is in [doc/InternalDocs.md](doc/InternalDocs.md) — read it
before touching the submodule or proposing a NuGet swap.

## Things That Will Trip You Up

- **`SessionSettings` is a `record`** — modify via `with { ... }`, then assign back to `definition.Settings` (see `MainWindow.PromptForSshSecretsAsync`).
- **Transient secret fields** are `[JsonIgnore]` *and* mutable on otherwise-immutable records — that's intentional.
- **`LegacyUiPreferences`** in [AppConfig.cs](src/Configuration/AppConfig.cs) exists only for one-shot migration; do not extend it.
- **No `dotnet test`** — there is no test project. Don't add one without asking.
- **`bin/`, `obj/`** are present in the workspace tree but generated; ignore them when searching.
