using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using TermThing.Configuration;

namespace TermThing.Views;

/// <summary>
/// Attaches additional mouse behaviours to a <see cref="TerminalControl"/>:
///
/// • RightClick (any modifiers) → context menu (Copy / Paste / Clear Scrollback).
///   Our tunnel handler intercepts all right-clicks so the menu is always shown.
///
/// • Ctrl+MouseWheel → adjust font size (clamp 6..36), persist to TempSettings.
///
/// Future ideas to investigate and layer in here:
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

    /// <summary>
    /// Attaches all mouse behaviours to <paramref name="tc"/>. Call once after
    /// <see cref="TerminalControl.LaunchProcess"/> returns.
    /// </summary>
    public static void Attach(TerminalControl tc)
    {
        // Tunneling handlers fire before the TerminalView's own bubbling handlers,
        // giving us the chance to handle (and optionally suppress) events first.
        tc.AddHandler(InputElement.PointerPressedEvent,  OnPointerPressed,      RoutingStrategies.Tunnel);
        tc.AddHandler(InputElement.PointerWheelChangedEvent, OnPointerWheel,    RoutingStrategies.Tunnel);
    }

    // -----------------------------------------------------------------------
    // Ctrl+Right-click → context menu
    // -----------------------------------------------------------------------

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TerminalControl tc) return;

        var props = e.GetCurrentPoint(tc).Properties;
        if (!props.IsRightButtonPressed) return;

        // Intercept ALL right-clicks so we always show our context menu.
        // This overrides the built-in direct paste/copy in TerminalView, providing
        // a more discoverable UX (explicit Copy / Paste items).
        e.Handled = true;

        var menu = BuildContextMenu(tc);
        menu.Open(tc);
    }

    private static ContextMenu BuildContextMenu(TerminalControl tc)
    {
        var view      = GetTerminalView(tc);
        var terminal  = tc.Terminal;
        bool hasSelection = terminal.Selection.HasSelection;

        var copyItem = new MenuItem { Header = "Copy", IsEnabled = hasSelection };
        copyItem.Click += async (_, _) =>
        {
            if (view != null) await view.CopyAsync();
        };

        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += async (_, _) =>
        {
            if (view != null) await view.PasteAsync();
        };

        // Clear Scrollback — investigate whether XTerm.Terminal exposes this API.
        // TODO: if a ClearScrollback() method is added to TerminalView/Terminal in the
        //       Iciclecreek.Avalonia.TerminalWindow library, wire it up here.
        var clearItem = new MenuItem
        {
            Header    = "Clear Scrollback",
            IsEnabled = false, // Not yet exposed by the underlying XTerm.Terminal
        };

        return new ContextMenu
        {
            Items = { copyItem, pasteItem, new Separator(), clearItem },
        };
    }

    private static Iciclecreek.Terminal.TerminalView? GetTerminalView(TerminalControl tc)
        => tc.GetVisualDescendants()
             .OfType<Iciclecreek.Terminal.TerminalView>()
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

        // Persist so the next session opens at the same size
        SettingsService.Temp.TerminalFontSize = newSize;
        SettingsService.SaveTemp();

        e.Handled = true; // Prevent normal scroll
    }
}
