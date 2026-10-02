using Avalonia.Media;
using Iciclecreek.Terminal;
using TermThing.Configuration;
using TermThing.Views;

namespace TermThing.Sessions.Launchers;

/// <summary>
/// Creates a <see cref="TerminalControl"/> styled the way every TermThing terminal is,
/// for a session's first pane and for each pane split off it later.
/// </summary>
internal static class TerminalFactory
{
    public static TerminalControl Create(SessionDefinition definition, Action? saveConfig)
    {
        // Use the persisted font size if one has been set via Ctrl+Wheel
        var fontSize = SettingsService.Temp.TerminalFontSize > 0
            ? SettingsService.Temp.TerminalFontSize
            : 14;

        var tc = new TerminalControl
        {
            Background = Brushes.Black,
            Foreground = Brushes.LightGray,
            FontFamily = FontFamily.Parse("fonts:CascadiaCode#Cascadia Code"),
            FontSize   = fontSize,
        };

        // Attach terminal mouse enhancements (Ctrl+RightClick menu, Ctrl+Wheel font size)
        TerminalContextMenuBehavior.Attach(tc, definition, saveConfig);
        return tc;
    }
}
