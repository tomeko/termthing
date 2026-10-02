using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using Material.Icons.Avalonia;
using TermThing.Panes;

namespace TermThing.Views;

/// <summary>
/// The pane controls on a window's toolbar, acting on one tab's panes: a TMUX / SESSION
/// label saying whose panes they are, then icon buttons for what can be done: detach
/// (tmux only), close the active pane (with more than one pane), split right and down.
/// In tmux the buttons are green, since tmux does the work. (Zoom has no button; it is
/// Alt+Shift+Z.)
/// <para>
/// The owner says which panes (<see cref="Panes"/>) and calls <see cref="Refresh"/> when
/// they may have changed: tab switches, the tab entering or leaving tmux, and
/// <see cref="PaneLayoutView.PanesChangedEvent"/>.
/// </para>
/// </summary>
public sealed class PaneToolbar : StackPanel
{
    private static readonly IBrush SessionBrush = new SolidColorBrush(Color.Parse("#8a8a8a"));

    private readonly Border _mode;
    private readonly TextBlock _modeText;
    private readonly Button _detach, _close, _splitRight, _splitDown;

    public PaneToolbar()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        IsVisible = false;

        _modeText = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _mode = new Border
        {
            Child = _modeText,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        _detach = IconButton(Icon(MaterialIconKind.ExitToApp));
        _close = IconButton(Icon(MaterialIconKind.CloseBoxOutline));
        _splitRight = IconButton(Icon(MaterialIconKind.ViewSplitVertical));
        _splitDown = IconButton(Icon(MaterialIconKind.ViewSplitHorizontal));

        _detach.Click += (_, _) => DetachTmux?.Invoke();
        _close.Click += (_, _) => Act(p => p.RequestCloseActive());
        _splitRight.Click += (_, _) => Split(SplitAxis.LeftRight);
        _splitDown.Click += (_, _) => Split(SplitAxis.TopBottom);

        Children.Add(_mode);
        Children.Add(_detach);
        Children.Add(_close);
        Children.Add(_splitRight);
        Children.Add(_splitDown);
    }

    /// <summary>The panes acted on (the selected tab's), or null when there are none to act on.</summary>
    public Func<PaneLayoutView?>? Panes { get; set; }

    /// <summary>Leaves tmux: the tab's tmux client detaches and the tab shows its shell again.</summary>
    public Action? DetachTmux { get; set; }

    /// <summary>Shows a short message, e.g. that the active pane is too small to split.</summary>
    public Action<string>? ShowMessage { get; set; }

    /// <summary>Shows what the panes allow right now.</summary>
    public void Refresh()
    {
        var panes = Panes?.Invoke();
        IsVisible = panes is { CanSplit: true };
        if (panes is null) return;

        bool tmux = panes.IsExternalLayout;
        bool several = panes.PaneCount > 1;
        string whose = tmux ? "tmux pane" : "pane";

        _modeText.Text = tmux ? "TMUX" : "SESSION";
        _modeText.Foreground = tmux ? Brushes.White : SessionBrush;
        _mode.Background = tmux ? TmuxBadge.ActiveBrush : Brushes.Transparent;
        _mode.BorderBrush = tmux ? TmuxBadge.ActiveBrush : SessionBrush;
        ToolTip.SetTip(_mode, tmux ? "These panes are tmux's: tmux splits, closes and zooms them." : "These panes are this session's own shells.");

        _detach.IsVisible = tmux && DetachTmux is not null;
        _close.IsVisible = several;
        ToolTip.SetTip(_detach, "Detach from tmux: back to this tab's shell. The tmux session keeps running.");
        ToolTip.SetTip(_close, $"Close the {whose} (Ctrl+Shift+W)");
        ToolTip.SetTip(_splitRight, $"Split the {whose} right (Alt+Shift+=)");
        ToolTip.SetTip(_splitDown, $"Split the {whose} down (Alt+Shift+-)");

        foreach (var button in new[] { _detach, _close, _splitRight, _splitDown })
            button.Background = tmux ? TmuxBadge.ActiveBrush : Brushes.Transparent;
    }

    private void Split(SplitAxis axis)
    {
        if (Panes?.Invoke() is not { CanSplit: true } panes) return;
        if (panes.CanSplitActive(axis))
            panes.RequestSplit(axis);   // the new pane takes focus
        else
        {
            ShowMessage?.Invoke("The active pane is too small to split.");
            FocusActive(panes);
        }
    }

    private void Act(Action<PaneLayoutView> action)
    {
        if (Panes?.Invoke() is not { } panes) return;
        action(panes);
        Refresh();
    }

    private static void FocusActive(PaneLayoutView panes) =>
        Dispatcher.UIThread.Post(() => panes.ActivePane.Focus(), DispatcherPriority.Input);

    private static MaterialIcon Icon(MaterialIconKind kind) => new() { Kind = kind, Width = 20, Height = 20 };

    private static Button IconButton(MaterialIcon icon) => new()
    {
        Content = icon,
        Padding = new Thickness(7, 5),
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
