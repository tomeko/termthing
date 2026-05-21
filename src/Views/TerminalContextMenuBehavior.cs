using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using TermThing.Configuration;
using TermThing.Sessions;

namespace TermThing.Views;

/// <summary>
/// Attaches mouse and paste-flow behaviours to a <see cref="TerminalControl"/>:
///
/// • RightClick (no modifier)        → paste (with confirmation dialog by default).
/// • RightClick (with text selected) → copy the selection (PuTTY-style).
/// • Ctrl+RightClick                 → context menu (Copy / Paste / Clear Scrollback).
/// • Ctrl+MouseWheel                 → adjust font size, persist to TempSettings.
///
/// The paste flow consults <see cref="AppSettings.SkipPasteConfirmation"/> (global)
/// and <see cref="SessionSettings.SkipPasteConfirmation"/> (per-session) and only
/// pops the <see cref="PasteConfirmDialog"/> when neither is set.
///
/// Future ideas to layer in here:
///   - Word-boundary double-click configuration (currently hard-coded in XTerm).
///   - "Copy as HTML" with ANSI colour preservation.
///   - In-terminal search (Ctrl+F) using XTerm's search API.
///   - Hyperlink detection (OSC 8 or regex-based) with Ctrl+Click.
///   - Middle-click paste (X11-style primary-selection paste).
///   - Configurable scroll lines per wheel notch.
/// </summary>
public static class TerminalContextMenuBehavior
{
    private const double MinFontSize = 6;
    private const double MaxFontSize = 36;

    private static readonly Dictionary<TerminalControl, AttachContext> _contexts = new();

    private sealed class AttachContext
    {
        public SessionDefinition? Definition;
        public Action? SaveConfig;
    }

    /// <summary>
    /// Attaches all mouse behaviours to <paramref name="tc"/>. Call once after the
    /// <see cref="TerminalControl"/> is constructed. Pass <paramref name="definition"/>
    /// and <paramref name="saveConfig"/> so the per-session paste-skip flag can be
    /// read and persisted from the paste confirmation dialog.
    /// </summary>
    public static void Attach(TerminalControl tc, SessionDefinition? definition = null, Action? saveConfig = null)
    {
        _contexts[tc] = new AttachContext { Definition = definition, SaveConfig = saveConfig };
        tc.DetachedFromVisualTree += (_, _) => _contexts.Remove(tc);

        // Show the standard text-selection (I-beam) cursor over the terminal so
        // it's obvious that text in here is selectable. The cursor automatically
        // reverts to the parent's default when the pointer leaves the control.
        tc.Cursor = new Cursor(StandardCursorType.Ibeam);

        // Tunneling handlers fire before the TerminalView's own bubbling handlers,
        // giving us the chance to handle (and optionally suppress) events first.
        tc.AddHandler(InputElement.PointerPressedEvent,      OnPointerPressed, RoutingStrategies.Tunnel);
        tc.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel,   RoutingStrategies.Tunnel);
    }

    // -----------------------------------------------------------------------
    // Right-click → paste-with-confirm, or copy if selection, or menu if Ctrl
    // -----------------------------------------------------------------------

    private static async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TerminalControl tc) return;

        var props = e.GetCurrentPoint(tc).Properties;
        if (!props.IsRightButtonPressed) return;

        // Always claim the event — we replace the submodule's default behaviour.
        e.Handled = true;

        var view     = GetTerminalView(tc);
        var terminal = tc.Terminal;
        bool ctrl    = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (ctrl)
        {
            // Ctrl+RightClick → show the explicit Copy/Paste/Clear menu.
            BuildContextMenu(tc).Open(tc);
            return;
        }

        if (terminal.Selection.HasSelection && view != null)
        {
            // RightClick with text selected → copy the selection (PuTTY-style).
            await CopyWithFeedbackAsync(tc, view);
            return;
        }

        // RightClick on no selection → paste, gated by confirmation settings.
        if (view != null)
            await RequestPasteAsync(tc, view);
    }

    private static ContextMenu BuildContextMenu(TerminalControl tc)
    {
        var view     = GetTerminalView(tc);
        var terminal = tc.Terminal;
        bool hasSelection = terminal.Selection.HasSelection;

        var copyItem = new MenuItem { Header = "Copy", IsEnabled = hasSelection };
        copyItem.Click += async (_, _) =>
        {
            if (view != null) await CopyWithFeedbackAsync(tc, view);
        };

        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += async (_, _) =>
        {
            if (view != null) await RequestPasteAsync(tc, view);
        };

        // Clear Scrollback — investigate whether XTerm.Terminal exposes this API.
        // TODO: if a ClearScrollback() method is added to TerminalView/Terminal in
        //       the Iciclecreek.Avalonia.TerminalWindow library, wire it up here.
        var clearItem = new MenuItem
        {
            Header    = "Clear Scrollback",
            IsEnabled = false,
        };

        return new ContextMenu
        {
            Items = { copyItem, pasteItem, new Separator(), clearItem },
        };
    }

    // -----------------------------------------------------------------------
    // Copy with visual feedback (clear selection + toast)
    // -----------------------------------------------------------------------

    private static async Task CopyWithFeedbackAsync(TerminalControl tc, TerminalView view)
    {
        var copied = await view.CopyAsync();
        if (!copied) return;
        view.ClearSelection();
        Toast.Show(tc, "Copied");
    }

    // -----------------------------------------------------------------------
    // Paste flow with confirmation
    // -----------------------------------------------------------------------

    private static async Task RequestPasteAsync(TerminalControl tc, TerminalView view)
    {
        var top = TopLevel.GetTopLevel(tc);
        var clipboard = top?.Clipboard;
        if (clipboard == null) return;

        var transfer = await clipboard.TryGetDataAsync();
        if (transfer == null) return;
        var text = await transfer.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;

        _contexts.TryGetValue(tc, out var ctx);
        var def = ctx?.Definition;

        bool skipGlobal  = SettingsService.App.SkipPasteConfirmation;
        bool skipSession = def?.Settings?.SkipPasteConfirmation == true;

        if (skipGlobal || skipSession)
        {
            await view.PasteAsync();
            return;
        }

        var owner = top as Window;
        if (owner == null)
        {
            // No window host → fall through to direct paste rather than silently dropping.
            await view.PasteAsync();
            return;
        }

        var dialog = new PasteConfirmDialog(text);
        var result = await dialog.ShowDialog<PasteConfirmResult?>(owner);
        if (result == null) return;

        if (result.SkipGlobally)
        {
            SettingsService.App.SkipPasteConfirmation = true;
            SettingsService.SaveApp();
        }

        if (result.SkipForSession && def?.Settings != null && ctx?.SaveConfig != null)
        {
            // SessionSettings is a record — mutate via `with` then reassign.
            def.Settings = def.Settings with { SkipPasteConfirmation = true };
            ctx.SaveConfig();
        }

        await view.PasteAsync();
    }

    private static TerminalView? GetTerminalView(TerminalControl tc)
        => tc.GetVisualDescendants()
             .OfType<TerminalView>()
             .FirstOrDefault();

    // -----------------------------------------------------------------------
    // Ctrl+Wheel → font size
    // -----------------------------------------------------------------------

    private static void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not TerminalControl tc) return;
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        var view = GetTerminalView(tc);
        if (view == null) return;

        var delta   = e.Delta.Y > 0 ? 1.0 : -1.0;
        var newSize = Math.Clamp(view.FontSize + delta, MinFontSize, MaxFontSize);
        view.FontSize = newSize;

        SettingsService.Temp.TerminalFontSize = newSize;
        SettingsService.SaveTemp();

        e.Handled = true;
    }
}
