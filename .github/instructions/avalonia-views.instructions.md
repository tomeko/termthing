---
description: "Use when authoring or editing Avalonia views (.axaml + code-behind .axaml.cs) in TermThing — covers compiled-bindings-off default, code-behind partial pattern, UI-thread marshalling, and dialog conventions."
applyTo: "src/Views/**/*.{axaml,axaml.cs}"
---
# Avalonia Views — TermThing Conventions

These rules apply only to files under [src/Views/](../../src/Views/).
General project rules live in [AGENTS.md](../../AGENTS.md).

## Bindings

- `AvaloniaUseCompiledBindingsByDefault` is **off** project-wide
  (see [src/TermThing.csproj](../../src/TermThing.csproj)).
- Default to **classic (reflection) bindings** — do not add `x:DataType` or
  `x:CompileBindings="True"` unless you have a deliberate reason and scope it
  per-control.
- Do not add a project-wide opt-in to compiled bindings without asking.

## Code-Behind Pattern

- Every `*.axaml` has a matching `partial class` in `*.axaml.cs` with an
  `InitializeComponent()` call in the constructor.
- Find named controls via `this.FindControl<T>("Name")` (already the project
  norm — see [`MainWindow.axaml.cs`](../../src/Views/MainWindow.axaml.cs)).
  Do not introduce `x:Name` field generation tweaks.
- File-scoped `namespace TermThing.Views;` — match existing files.

## UI-Thread Marshalling

- Any callback originating from a session/launcher/SSH/serial worker must be
  posted back via `Avalonia.Threading.Dispatcher.UIThread.Post(...)` before
  touching controls. See `LocalSessionInstance` in
  [LocalSessionLauncher.cs](../../src/Sessions/Launchers/LocalSessionLauncher.cs)
  for the canonical pattern.

## Dialogs

- Modal dialogs are plain `Window` subclasses shown with
  `await dialog.ShowDialog<TResult>(this)` from `MainWindow`.
- Return typed results (`bool?`, `string?`, a settings record) via
  `Window.Close(result)` — do not expose mutable public fields as the result
  channel unless an existing dialog already does so (e.g. `SshConnectDialog`
  exposes `Password` / `KeyPassphrase`).
- Center on owner: `WindowStartupLocation = WindowStartupLocation.CenterOwner`.
- Honour `Enter` (commit) and `Escape` (cancel) on the primary input.

## Terminal Re-Parenting

- When moving a `TerminalControl` between windows (floating ↔ docked),
  always wrap the swap in `BeginReparent()` / `EndReparent()` and post the
  `EndReparent()` call via `Dispatcher.UIThread.Post(..., DispatcherPriority.Loaded)`.
- Never set `Content = null` on a tab/window holding a live terminal without
  first calling `BeginReparent()` — it will kill the PTY. Background in
  [doc/InternalDocs.md](../../doc/InternalDocs.md).

## Secrets in Views

- Dialog code may *capture* SSH passwords / passphrases into local fields
  but must hand them to the launcher via the transient `[JsonIgnore]` fields
  on the `SessionSettings` record (see `MainWindow.PromptForSshSecretsAsync`).
- Never assign secrets to persisted properties of `SessionDefinition` /
  `SessionSettings`.

## Persistence Triggers

- UI state that should survive restart (column widths, window size,
  recent-session list, last-used new-session kind) belongs in
  [`TempSettings`](../../src/Configuration/TempSettings.cs); call
  `SettingsService.SaveTemp()` after mutating it.
- User-curated preferences belong in
  [`AppSettings`](../../src/Configuration/AppSettings.cs); call
  `SettingsService.SaveApp()`.
- The sessions tree itself is persisted by `MainWindow` via
  `ConfigStore.Save(_config)` after structural edits.

## Styling

- Fonts: terminal content uses `fonts:CascadiaCode#Cascadia Code`; UI uses
  the default Inter from the Fluent theme — don't hard-code alternate fonts.
- Don't introduce new theme resources / brushes ad-hoc; reuse Fluent theme
  tokens where possible.
