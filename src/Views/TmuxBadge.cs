using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace TermThing.Views;

/// <summary>What an SSH tab's TMUX badge shows.</summary>
public enum TmuxBadgeState
{
    /// <summary>A shell; tmux may or may not be installed (not probed yet).</summary>
    Off,
    /// <summary>The tab shows a tmux session through control mode.</summary>
    Control,
    /// <summary>The tab's shell runs a tmux client itself (legacy "typed into the shell").</summary>
    Legacy,
    /// <summary>tmux isn't installed on the host.</summary>
    Unavailable,
    /// <summary>The session is disconnected.</summary>
    Disconnected,
}

/// <summary>
/// The small "TMUX" label on an SSH tab's header (and its floating window's toolbar):
/// outlined for a shell, filled green when the tab shows a tmux session. Clicking it
/// opens the tab's tmux menu.
/// </summary>
public sealed class TmuxBadge : Border
{
    /// <summary>The green of a tab that is in tmux; also used by the pane controls (<see cref="PaneToolbar"/>).</summary>
    public static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#2e7d32"));
    private static readonly IBrush ActiveBorderBrush = new SolidColorBrush(Color.Parse("#43a047"));
    private static readonly IBrush LegacyBrush = new SolidColorBrush(Color.Parse("#66bb6a"));
    private static readonly IBrush OffBrush = new SolidColorBrush(Color.Parse("#8a8a8a"));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#505050"));

    private readonly TextBlock _text;

    public TmuxBadge()
    {
        _text = new TextBlock
        {
            Text = "TMUX",
            FontSize = 9.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = _text;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(3);
        Padding = new Thickness(4, 1);
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;   // not the start of a tab drag
            Clicked?.Invoke(this, EventArgs.Empty);
        };
        SetState(TmuxBadgeState.Off, null);
    }

    /// <summary>Raised on a left click.</summary>
    public event EventHandler? Clicked;

    public TmuxBadgeState State { get; private set; }

    /// <param name="sessionName">The tmux session shown (control mode) or attached in the shell (legacy).</param>
    public void SetState(TmuxBadgeState state, string? sessionName)
    {
        State = state;
        (Background, BorderBrush, _text.Foreground) = state switch
        {
            TmuxBadgeState.Control => (ActiveBrush, ActiveBorderBrush, (IBrush)Brushes.White),
            TmuxBadgeState.Legacy => (Brushes.Transparent, LegacyBrush, LegacyBrush),
            TmuxBadgeState.Off => (Brushes.Transparent, OffBrush, OffBrush),
            _ => (Brushes.Transparent, DimBrush, DimBrush),
        };
        ToolTip.SetTip(this, state switch
        {
            TmuxBadgeState.Control => $"tmux session '{sessionName}' (control mode). Click for tmux actions.",
            TmuxBadgeState.Legacy => $"The shell is attached to tmux session '{sessionName}' (legacy). Click for tmux actions.",
            TmuxBadgeState.Unavailable => "tmux was not found on this host.",
            TmuxBadgeState.Disconnected => "Disconnected.",
            _ => "Not in tmux. Click to attach a tmux session in this tab.",
        });
    }

}
