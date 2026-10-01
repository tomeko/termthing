using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using Material.Icons;
using Material.Icons.Avalonia;
using TermThing.Configuration;
using TermThing.Panes;

namespace TermThing.Views;

public partial class FloatingSessionWindow : Window
{
    private readonly Action _dockBackCallback;
    private readonly Func<Task> _closeCallback;
    private readonly TextBlock _titleSource;
    private readonly Func<PaneLayoutView?> _panes;

    // Prevents the Closing handler from triggering a second dock-back
    // after DockBackSession has already called Close() on this window.
    private bool _isDocking;

    public FloatingSessionWindow(
        Panel sessionHost,
        Control? sftpPanel,
        TextBlock titleSource,
        MaterialIconKind iconKind,
        IBrush iconBrush,
        Action dockBackCallback,
        Func<Task> closeCallback,
        Action<Control, PlacementMode> openTabMenu,
        Func<PaneLayoutView?> panes)
    {
        InitializeComponent();

        _dockBackCallback = dockBackCallback;
        _closeCallback = closeCallback;
        _titleSource = titleSource;
        _panes = panes;

        // Set toolbar icon.
        var titleIcon = this.FindControl<MaterialIcon>("TitleIcon")!;
        titleIcon.Kind       = iconKind;
        titleIcon.Foreground = iconBrush;

        // Sync window title and toolbar label from the session's title block.
        Title = titleSource.Text;
        TitleText.Text = titleSource.Text;
        _titleSource.PropertyChanged += OnTitleSourcePropertyChanged;

        // Place the session host panel (terminal + any overlays) in the terminal slot.
        TerminalHost.Content = sessionHost;

        // If the session has an SFTP browser, show it in the left panel.
        SetSftpPanel(sftpPanel);

        DockBackButton.Click += OnDockBackClicked;

        // Tab menu: the ▾ button, the icon, or a right-click anywhere on the toolbar.
        MenuButton.Click += (_, _) => openTabMenu(MenuButton, PlacementMode.BottomEdgeAlignedLeft);
        titleIcon.Tapped += (_, _) => openTabMenu(titleIcon, PlacementMode.BottomEdgeAlignedLeft);
        void OnToolbarContext(object? sender, ContextRequestedEventArgs e)
        {
            e.Handled = true;
            openTabMenu((Control)sender!, PlacementMode.Pointer);
        }
        Toolbar.ContextRequested     += OnToolbarContext;
        ToolbarBand.ContextRequested += OnToolbarContext;

        // Quick split buttons; shown only for sessions that can split (the instance
        // can change on reconnect, so re-checked whenever the window is activated).
        SplitRightButton.Click += (_, _) => _panes()?.RequestSplit(SplitAxis.LeftRight);
        SplitDownButton.Click  += (_, _) => _panes()?.RequestSplit(SplitAxis.TopBottom);
        UpdateSplitButtons();
        Closing += OnWindowClosing;

        // When this floating window regains focus, hand it to the terminal so the
        // user can type immediately without clicking into it first.
        Activated += OnWindowActivated;
    }

    /// <summary>
    /// Shows <paramref name="panel"/> (the session's SFTP browser) in the left column,
    /// or collapses the column when null — on float, and whenever SFTP is opened or
    /// closed, or the session disconnects or reconnects, while floating.
    /// </summary>
    internal void SetSftpPanel(Control? panel)
    {
        SftpHost.Content = panel;
        SftpHost.IsVisible = SftpSplitter.IsVisible = panel is not null;

        var columns = ContentGrid.ColumnDefinitions;
        if (panel is not null)
        {
            // Restore the saved SFTP panel width (same setting used by the main window).
            var savedWidth = SettingsService.Temp.LeftColumnWidthPx;
            columns[0].Width = new GridLength(savedWidth > 0 ? savedWidth : 260, GridUnitType.Pixel);
            columns[1].Width = new GridLength(4, GridUnitType.Pixel);
        }
        else
        {
            // No SFTP browser: collapse its columns so the terminal (and the title over
            // it) starts at the window's left edge instead of after an empty gap.
            columns[0].Width = new GridLength(0);
            columns[1].Width = new GridLength(0);
        }
    }

    private void UpdateSplitButtons() =>
        SplitRightButton.IsVisible = SplitDownButton.IsVisible = _panes() is not null;

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        UpdateSplitButtons();
        var terminal = TerminalHost.GetVisualDescendants().OfType<TerminalControl>().FirstOrDefault();
        if (terminal is null) return;
        if (terminal.IsLoaded)
            terminal.Focus();
        else
            terminal.Loaded += FocusOnce;

        void FocusOnce(object? s, RoutedEventArgs _)
        {
            terminal.Loaded -= FocusOnce;
            terminal.Focus();
        }
    }

    private void OnSftpSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        SettingsService.Temp.LeftColumnWidthPx = ContentGrid.ColumnDefinitions[0].ActualWidth;
        SettingsService.SaveTemp();
    }

    /// <summary>
    /// Detaches hosted controls from this window's visual tree so they can be
    /// re-parented back into the main window without Avalonia parent conflicts.
    /// Also unsubscribes the title-change listener.
    /// </summary>
    internal void DetachContents()
    {
        _titleSource.PropertyChanged -= OnTitleSourcePropertyChanged;
        TerminalHost.Content = null;
        SftpHost.Content = null;
    }

    /// <summary>
    /// Closes this window unconditionally (no dock-back). Used when the session
    /// is explicitly closed while floating via <c>CloseTab</c>.
    /// </summary>
    internal void ForceClose()
    {
        _isDocking = true;
        DetachContents();
        Close();
    }

    private void OnTitleSourcePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
        {
            var text = _titleSource.Text;
            Title = text;
            TitleText.Text = text;
        }
    }

    /// <summary>Docks the session back into the main window (same as the Dock Back button).</summary>
    internal void RequestDockBack() => OnDockBackClicked(this, new RoutedEventArgs());

    private void OnDockBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_isDocking) return;
        _isDocking = true;
        // Defer so we're not calling back into MainWindow while still inside this button handler.
        Dispatcher.UIThread.Post(_dockBackCallback);
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // If we're already docking or force-closing, let the close proceed.
        if (_isDocking) return;

        // Always cancel the OS close; we handle the outcome ourselves via the dialog.
        e.Cancel = true;

        var result = await ShowCloseDialogAsync();

        if (result == CloseDialogResult.Close)
        {
            _isDocking = true;
            await _closeCallback();
        }
        else if (result == CloseDialogResult.ReAttach)
        {
            _isDocking = true;
            Dispatcher.UIThread.Post(_dockBackCallback);
        }
        // null = cancelled → window stays open.
    }

    private enum CloseDialogResult { Close, ReAttach }

    private Task<CloseDialogResult?> ShowCloseDialogAsync()
    {
        var tcs = new TaskCompletionSource<CloseDialogResult?>();

        var closeBtn = new Button
        {
            Content = "Close session",
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
        };
        var reattachBtn = new Button
        {
            Content = "Re-attach to main window",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(0, 8),
        };

        var dialog = new Window
        {
            Title = "Close session?",
            SizeToContent = SizeToContent.Height,
            Width = 320,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Content = new StackPanel
            {
                Margin = new Thickness(20, 16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "What would you like to do with this session?",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White,
                        Margin = new Thickness(0, 0, 0, 4),
                    },
                    closeBtn,
                    reattachBtn,
                    cancelBtn,
                }
            }
        };

        closeBtn.Click    += (_, _) => { tcs.TrySetResult(CloseDialogResult.Close);    dialog.Close(); };
        reattachBtn.Click += (_, _) => { tcs.TrySetResult(CloseDialogResult.ReAttach); dialog.Close(); };
        cancelBtn.Click   += (_, _) => { tcs.TrySetResult(null);                        dialog.Close(); };
        dialog.Closed     += (_, _) => tcs.TrySetResult(null);
        dialog.Opened     += (_, _) => closeBtn.Focus(NavigationMethod.Tab);

        dialog.ShowDialog(this);
        return tcs.Task;
    }
}
