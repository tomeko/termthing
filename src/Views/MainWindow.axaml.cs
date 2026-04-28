using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TermThing.Configuration;
using TermThing.Editor;
using TermThing.Sessions;
using TermThing.Sessions.Launchers;
using TermThing.Ssh;

namespace TermThing.Views;

public partial class MainWindow : Window, ISessionPromptHost
{
    private readonly SessionLauncherRegistry _registry = new();
    private readonly IKnownHostsService _knownHosts = new KnownHostsService();
    private readonly EditorRegistry _editors = new();
    private AppConfig _config = new();

    // Maps TabItem → TabState so we can manage overlays, reconnect, and teardown cleanly.
    private readonly Dictionary<TabItem, TabState> _tabStates = new();

    // Session IDs currently connecting — prevents double-click from starting two sessions
    private readonly HashSet<Guid> _launching = new();

    private sealed class TabState
    {
        public required TabItem Tab { get; init; }
        public required Panel Host { get; init; }
        public required TextBlock TitleBlock { get; init; }
        public required SessionDefinition Def { get; init; }
        public ISessionInstance? Instance { get; set; }
        public SessionEndedOverlay? Overlay { get; set; }
        public FloatingSessionWindow? FloatingWindow { get; set; }
        /// <summary>
        /// Stable editor-session ID sourced from <see cref="SftpFileBrowserView.SessionEditorId"/>.
        /// Null for sessions without an SFTP browser.
        /// </summary>
        public Guid? SftpEditorSessionId { get; init; }
    }

    // Provides access to the named grid for column-width persistence
    private Grid? _mainBodyGrid;
    private MenuFlyout? _newSessionFlyout;

    public MainWindow()
    {
        InitializeComponent();

        _registry.Register(new LocalSessionLauncher());
        _registry.Register(new SshSessionLauncher(_knownHosts, () => _config, _editors, SaveConfig));
        _registry.Register(new SerialSessionLauncher());

        // Load both settings files
        SettingsService.Load();

        // Detect available local shells (probes filesystem once; Git Bash check included).
        LocalShells.Detect();

        _config = ConfigStore.LoadOrDefault();

        // One-shot migration: copy legacy UiPreferences → TempSettings
        if (_config.UiPreferences is { } legacy)
        {
            if (legacy.LeftColumnWidth > 0)
                SettingsService.Temp.LeftColumnWidthPx = legacy.LeftColumnWidth;
            if (legacy.WindowWidth > 0)
                SettingsService.Temp.WindowWidth = legacy.WindowWidth;
            if (legacy.WindowHeight > 0)
                SettingsService.Temp.WindowHeight = legacy.WindowHeight;
            SettingsService.Temp.ActiveLeftTab = legacy.ActiveLeftTab;
            _config.UiPreferences = null; // clear so it's not re-migrated
            SettingsService.SaveTemp();
            ConfigStore.Save(_config);
        }

        SessionTree.SetRoot(_config.RootGroup);
        SessionTree.SessionLaunchRequested += OnSessionLaunchRequested;
        SessionTree.SessionEditRequested   += OnSessionEditRequestedAsync;
        SessionTree.TreeChanged += OnTreeChanged;

        // Restore layout from TempSettings once the window is loaded
        this.Loaded += OnWindowLoaded;
    }

    // -----------------------------------------------------------------------
    // Window loaded — restore layout from TempSettings
    // -----------------------------------------------------------------------

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        _mainBodyGrid = this.FindControl<Grid>("MainBodyGrid");
        // MenuFlyout is not a Control; retrieve it from the SplitButton instead
        var splitBtn = this.FindControl<SplitButton>("NewSessionButton");
        _newSessionFlyout = splitBtn?.Flyout as MenuFlyout;

        // Wire the flyout Opening event for recent-sessions rebuilding
        if (_newSessionFlyout != null)
            _newSessionFlyout.Opening += OnNewSessionFlyoutOpening;

        var temp = SettingsService.Temp;

        // Restore left-column width
        if (_mainBodyGrid != null && temp.LeftColumnWidthPx > 0)
            _mainBodyGrid.ColumnDefinitions[0].Width = new GridLength(temp.LeftColumnWidthPx, GridUnitType.Pixel);

        // Restore window size, then maximized state
        if (temp.WindowWidth > 0)  Width  = temp.WindowWidth;
        if (temp.WindowHeight > 0) Height = temp.WindowHeight;
        if (temp.WindowMaximized)  WindowState = WindowState.Maximized;

        // Always open on Sessions tab (index 0) regardless of last-used tab
        if (LeftTabs != null)
            LeftTabs.SelectedIndex = 0;
    }

    // -----------------------------------------------------------------------
    // ISessionPromptHost
    // -----------------------------------------------------------------------

    public async Task<bool> PromptForSshSecretsAsync(SessionDefinition definition)
    {
        var settings = (SshSettings)definition.Settings!;
        var dialog = new SshConnectDialog(editMode: false, prefill: settings);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true) return false;

        // Mutate the transient fields — not persisted
        settings.TransientPassword = dialog.Password;
        settings.TransientKeyPassphrase = dialog.KeyPassphrase;
        definition.Settings = settings with { };
        return true;
    }

    public async Task<string?> PromptForPassphraseAsync(string keyFilePath, string? hostname = null)
    {
        var fileName = System.IO.Path.GetFileName(keyFilePath);

        var passBox = new TextBox
        {
            PasswordChar = '●',
            Watermark = "Passphrase",
            MinWidth = 260,
        };

        var okBtn     = new Button { Content = "OK",     HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0,0,8,0) };
        var cancelBtn = new Button { Content = "Cancel", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };

        var win = new Window
        {
            Title = string.IsNullOrWhiteSpace(hostname) ? "Key Passphrase" : $"Key Passphrase — {hostname}",
            Width = 380,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin  = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Enter passphrase for key:",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = fileName,
                        FontFamily = new Avalonia.Media.FontFamily("Cascadia Code,Consolas,monospace"),
                        FontSize   = 12,
                        TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                        Foreground = Avalonia.Media.Brushes.Gray,
                    },
                    passBox,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { okBtn, cancelBtn },
                    },
                },
            },
        };

        okBtn.Click     += (_, _) => win.Close(passBox.Text ?? string.Empty);
        cancelBtn.Click += (_, _) => win.Close(null);

        passBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; win.Close(passBox.Text ?? string.Empty); }
        };

        win.Opened += (_, _) => passBox.Focus();

        return await win.ShowDialog<string?>(this);
    }

    // -----------------------------------------------------------------------
    // Top toolbar
    // -----------------------------------------------------------------------

    private void OnOverflowClicked(object? sender, RoutedEventArgs e) { /* flyout handles it */ }
    private void OnSaveClicked(object? sender, RoutedEventArgs e) => SaveConfig();

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow();
        await win.ShowDialog(this);
    }

    /// <summary>
    /// Primary New Session click — opens the last-used session kind dialog.
    /// </summary>
    private async void OnNewSessionPrimaryClicked(object? sender, RoutedEventArgs e)
    {
        switch (SettingsService.Temp.LastNewSessionKind)
        {
            case SessionKind.Ssh:    await DoNewSshAsync();    break;
            case SessionKind.Serial: await DoNewSerialAsync(); break;
            default:                 await DoNewLocalAsync();  break;
        }
    }

    /// <summary>
    /// Rebuilds the "recent sessions" items appended below the separator in the
    /// New-Session split-button flyout each time it opens.
    /// </summary>
    private void OnNewSessionFlyoutOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout flyout) return;

        // Keep only the static first 3 items (Local / SSH / Serial) + a separator we add
        while (flyout.Items.Count > 3) flyout.Items.RemoveAt(flyout.Items.Count - 1);

        var recentIds = SettingsService.Temp.RecentSessionIds;
        var cap = Math.Min(SettingsService.App.RecentSessionsCount, recentIds.Count);
        if (cap == 0) return;

        flyout.Items.Add(new Separator());

        int shown = 0;
        foreach (var id in recentIds)
        {
            if (shown >= cap) break;
            var def = FindSessionById(id, _config.RootGroup);
            if (def == null) continue; // deleted session — skip

            var item = new MenuItem { Header = def.Name };
            var captured = def;
            item.Click += async (_, _) => await LaunchAndAddTabAsync(captured);
            flyout.Items.Add(item);
            shown++;
        }
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_mainBodyGrid == null) return;
        SettingsService.Temp.LeftColumnWidthPx = _mainBodyGrid.ColumnDefinitions[0].ActualWidth;
        SettingsService.SaveTemp();
    }

    private async void OnNewLocalClicked(object? sender, RoutedEventArgs e)
    {
        SettingsService.Temp.LastNewSessionKind = SessionKind.Local;
        await DoNewLocalAsync();
    }

    private async void OnNewSshClicked(object? sender, RoutedEventArgs e)
    {
        SettingsService.Temp.LastNewSessionKind = SessionKind.Ssh;
        await DoNewSshAsync();
    }

    private async void OnNewSerialClicked(object? sender, RoutedEventArgs e)
    {
        SettingsService.Temp.LastNewSessionKind = SessionKind.Serial;
        await DoNewSerialAsync();
    }

    private async Task DoNewLocalAsync()
    {
        LocalShells.ShellOption shell;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: let the user pick from available shells.
            var dialog = new LocalShellPickerDialog();
            var result = await dialog.ShowDialog<bool?>(this);
            if (result != true || dialog.SelectedShell is null) return;
            shell = dialog.SelectedShell;
        }
        else
        {
            // Linux / macOS: skip the dialog and auto-launch the detected default shell.
            shell = LocalShells.Default;
        }

        var def = new SessionDefinition
        {
            Name = shell.Label,
            Kind = SessionKind.Local,
            Settings = new LocalSettings
            {
                Process = shell.Process,
                Args    = shell.Args,
            },
        };

        await LaunchAndAddTabAsync(def);
    }

    private async Task DoNewSshAsync()
    {
        var dialog = new SshConnectDialog(editMode: false);
        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true || string.IsNullOrWhiteSpace(dialog.Host)) return;

        var settings = new SshSettings
        {
            Host = dialog.Host!,
            Port = dialog.Port,
            Username = dialog.Username!,
            KeyFilePath = dialog.KeyFile,
            EnableSftp = dialog.EnableSftp,
            TransientPassword = dialog.Password,
            TransientKeyPassphrase = dialog.KeyPassphrase,
            JumpHosts = [.. dialog.JumpHosts],
        };

        var sessionName = string.IsNullOrWhiteSpace(dialog.SessionName)
            ? $"{dialog.Username}@{dialog.Host}"
            : dialog.SessionName;
        var def = new SessionDefinition
        {
            Name = sessionName,
            Kind = SessionKind.Ssh,
            Settings = settings,
        };

        if (dialog.SaveAsSession)
        {
            bool alreadyExists = _config.RootGroup.Sessions.Any(s =>
                s.Settings is SshSettings ss &&
                ss.Host == settings.Host &&
                ss.Port == settings.Port &&
                ss.Username == settings.Username);

            if (!alreadyExists)
            {
                _config.RootGroup.Sessions.Add(def);
                SessionTree.SetRoot(_config.RootGroup);
                SaveConfig();
            }
        }

        await LaunchAndAddTabAsync(def);
    }

    private async Task DoNewSerialAsync()
    {
        var dialog = new SerialConnectDialog();
        await dialog.ShowDialog<bool?>(this);
        // Serial is not yet implemented; the dialog explains this.
    }

    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import sessions",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("TermThing JSON") { Patterns = ["*.json"] }],
        });

        var picked = files?.FirstOrDefault();
        if (picked == null) return;

        try
        {
            var path = picked.TryGetLocalPath() ?? picked.Path.LocalPath;
            var imported = SessionImportExport.Import(path);
            _config.RootGroup.Subgroups.Add(imported);
            SessionTree.SetRoot(_config.RootGroup);
            SaveConfig();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Import failed", ex.Message);
        }
    }

    private async void OnExportClicked(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export sessions",
            SuggestedFileName = "termthing-sessions.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("TermThing JSON") { Patterns = ["*.json"] }],
        });

        if (file == null) return;

        try
        {
            var path = file.TryGetLocalPath() ?? file.Path.LocalPath;
            SessionImportExport.Export(_config.RootGroup, path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Export failed", ex.Message);
        }
    }

    private async void OnManageKnownHostsClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new KnownHostsManagerDialog(_knownHosts);
        await dialog.ShowDialog(this);
    }

    // -----------------------------------------------------------------------
    // Session tree requests
    // -----------------------------------------------------------------------

    private async void OnSessionLaunchRequested(object? sender, SessionDefinition def)
    {
        // Drop the event if this session is already connecting (double-click protection)
        if (!_launching.Add(def.Id)) return;
        try
        {
            await LaunchAndAddTabAsync(def);
            SaveConfig();
        }
        finally
        {
            _launching.Remove(def.Id);
        }
    }

    private async void OnSessionEditRequestedAsync(object? sender, SessionDefinition def)
    {
        if (def.Kind != SessionKind.Ssh || def.Settings is not SshSettings sshSettings) return;

        var dialog = new SshConnectDialog(
            editMode  : true,
            prefill   : sshSettings,
            appConfig : _config);

        var result = await dialog.ShowDialog<bool?>(this);
        if (result != true) return;

        var updated = sshSettings with
        {
            Host        = dialog.Host        ?? sshSettings.Host,
            Port        = dialog.Port,
            Username    = dialog.Username    ?? sshSettings.Username,
            KeyFilePath = dialog.KeyFile,
            EnableSftp  = dialog.EnableSftp,
            JumpHosts   = [.. dialog.JumpHosts],
        };

        if (!string.IsNullOrWhiteSpace(dialog.SessionName))
            def.Name = dialog.SessionName;

        def.Settings = updated;
        SessionTree.SetRoot(_config.RootGroup);
        SaveConfig();
    }

    private void OnTreeChanged(object? sender, EventArgs e)
    {
        // Prune deleted sessions from the recent-sessions list
        var allIds = CollectAllSessionIds(_config.RootGroup);
        SettingsService.Temp.RecentSessionIds.RemoveAll(id => !allIds.Contains(id));
        SettingsService.SaveTemp();
        SaveConfig();
    }

    // -----------------------------------------------------------------------
    // Tab management
    // -----------------------------------------------------------------------

    private async Task LaunchAndAddTabAsync(SessionDefinition def)
    {
        // Show a placeholder tab immediately so the user sees instant feedback.
        var connectingTab = AddConnectingTab(def.Name);
        try
        {
            var launcher = _registry.Get(def.Kind);
            var instance = await launcher.LaunchAsync(def, this);
            TerminalTabs.Items.Remove(connectingTab);
            AddTab(instance, def, def.Name);

            // Track in recent sessions if this is a saved session
            if (FindSessionById(def.Id, _config.RootGroup) != null)
            {
                var recent = SettingsService.Temp.RecentSessionIds;
                recent.Remove(def.Id);
                recent.Insert(0, def.Id);
                // Cap the list
                var max = Math.Max(1, SettingsService.App.RecentSessionsCount);
                while (recent.Count > max) recent.RemoveAt(recent.Count - 1);
                SettingsService.SaveTemp();
            }
        }
        catch (OperationCanceledException)
        {
            TerminalTabs.Items.Remove(connectingTab);
            // User cancelled — no error shown
        }
        catch (NotImplementedException ex)
        {
            TerminalTabs.Items.Remove(connectingTab);
            await ShowErrorAsync("Not implemented", ex.Message);
        }
        catch (Exception ex)
        {
            TerminalTabs.Items.Remove(connectingTab);
            await ShowErrorAsync("Failed to launch session", ex.Message);
        }
    }

    /// <summary>
    /// Inserts a transient "Connecting…" tab and selects it so the user gets
    /// immediate visual feedback before the async connect work starts.
    /// </summary>
    private TabItem AddConnectingTab(string sessionName)
    {
        var bar = new ProgressBar
        {
            IsIndeterminate = true,
            Width            = 220,
            Height           = 6,
        };
        var label = new TextBlock
        {
            Text                = $"Connecting to {sessionName}…",
            FontSize            = 13,
            Foreground          = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var content = new StackPanel
        {
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing             = 14,
            Children            = { bar, label },
        };
        var tab = new TabItem
        {
            Header  = $"⏳ {sessionName}",
            Content = content,
        };
        TerminalTabs.Items.Add(tab);
        TerminalTabs.SelectedItem = tab;
        return tab;
    }

    private void AddTab(ISessionInstance instance, SessionDefinition def, string initialTitle)
    {
        var titleBlock = new TextBlock
        {
            Text = initialTitle,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };

        var popOutButton = new Button
        {
            Content = "⤢",
            Padding = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
        };
        ToolTip.SetTip(popOutButton, "Float in separate window");

        var closeButton = new Button
        {
            Content = "×",
            Padding = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { titleBlock, popOutButton, closeButton },
        };

        // Wrap the terminal in a Panel so we can layer the disconnect overlay on top.
        var host = new Panel();
        host.Children.Add(instance.TabContent);

        var tab = new TabItem
        {
            Header = header,
            Content = host,
        };

        var state = new TabState
        {
            Tab        = tab,
            Host       = host,
            TitleBlock = titleBlock,
            Def        = def,
            Instance   = instance,
            // Capture the stable editor-session ID so we can close
            // any open editor windows when this tab is closed.
            SftpEditorSessionId = (instance.SftpPanel as SftpFileBrowserView)?.SessionEditorId,
        };
        _tabStates[tab] = state;

        WireSessionInstance(state, instance);
        popOutButton.Click += (_, _) => PopOutTab(tab);
        closeButton.Click  += async (_, _) => await CloseTabAsync(tab);

        TerminalTabs.Items.Add(tab);
        TerminalTabs.SelectedItem = tab;

        FocusTerminal(instance.TabContent);
    }

    private void WireSessionInstance(TabState state, ISessionInstance instance)
    {
        // Track title changes from the terminal (OSC title sequences)
        if (instance.TabContent is TerminalControl tc)
        {
            TerminalView.AddTitleChangedHandler(tc, (_, e) =>
            {
                if (!e.Handled) { state.TitleBlock.Text = e.Title; e.Handled = true; }
            });
        }

        instance.SessionEnded += (_, _) =>
        {
            state.Instance = null;
            instance.Dispose();
            // Session died naturally (SSH disconnect) — editors can no longer upload.
            if (state.SftpEditorSessionId.HasValue)
                _editors.NotifySessionEnded(state.SftpEditorSessionId.Value);
            Dispatcher.UIThread.Post(() => ShowDisconnectOverlay(state));
        };
    }

    private void ShowDisconnectOverlay(TabState state)
    {
        if (!_tabStates.ContainsKey(state.Tab)) return;

        var overlay = new SessionEndedOverlay();
        state.Overlay = overlay;
        state.Host.Children.Add(overlay);
        ClearSftpPanelIfNeeded(state.Tab);

        overlay.CloseRequested     += async (_, _) => await CloseTabAsync(state.Tab);
        overlay.ReconnectRequested += async (_, _) => await ReconnectTabAsync(state);
    }

    private async Task ReconnectTabAsync(TabState state)
    {
        if (!_tabStates.ContainsKey(state.Tab)) return;

        // Remove overlay and show a "Reconnecting…" indicator.
        state.Overlay = null;
        state.Host.Children.Clear();

        var bar   = new ProgressBar { IsIndeterminate = true, Width = 220, Height = 6 };
        var label = new TextBlock
        {
            Text                = $"Reconnecting to {state.Def.Name}…",
            FontSize            = 13,
            Foreground          = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        state.Host.Children.Add(new StackPanel
        {
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing             = 14,
            Children            = { bar, label },
        });

        ISessionInstance? newInstance = null;
        string? errorMessage = null;
        try
        {
            newInstance = await _registry.Get(state.Def.Kind).LaunchAsync(state.Def, this);
        }
        catch (OperationCanceledException)
        {
            // User cancelled credentials — put the overlay back without an error message.
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        // Abort if the tab was closed while we were connecting.
        if (!_tabStates.ContainsKey(state.Tab))
        {
            newInstance?.Dispose();
            return;
        }

        state.Host.Children.Clear();

        if (newInstance is null)
        {
            // Reconnect failed or was cancelled — show overlay again (with optional reason).
            var overlay = new SessionEndedOverlay(errorMessage);
            state.Overlay = overlay;
            state.Host.Children.Add(overlay);
            overlay.CloseRequested     += async (_, _) => await CloseTabAsync(state.Tab);
            overlay.ReconnectRequested += async (_, _) => await ReconnectTabAsync(state);
            return;
        }

        // Success — swap in the new terminal.
        state.Instance = newInstance;
        state.Host.Children.Add(newInstance.TabContent);
        WireSessionInstance(state, newInstance);
        FocusTerminal(newInstance.TabContent);
    }

    private async Task CloseTabAsync(TabItem tab)
    {
        if (!_tabStates.TryGetValue(tab, out var state)) return;
        _tabStates.Remove(tab);

        // Close any open editor windows before killing the SFTP client, giving
        // the user a chance to upload dirty buffers while the connection is alive.
        if (state.SftpEditorSessionId.HasValue)
            await _editors.CloseSessionEditorsAsync(state.SftpEditorSessionId.Value);

        state.Instance?.Kill();
        state.Instance?.Dispose();

        // If the session is floating, force-close the floating window without docking back.
        if (state.FloatingWindow != null)
        {
            state.FloatingWindow.ForceClose();
            state.FloatingWindow = null;
        }

        TerminalTabs.Items.Remove(tab);
        ClearSftpPanelIfNeeded(tab);
    }

    /// <summary>
    /// Removes the tab from the main strip and opens it as an independent floating window.
    /// </summary>
    private void PopOutTab(TabItem tab)
    {
        if (!_tabStates.TryGetValue(tab, out var state)) return;
        if (state.FloatingWindow != null) return; // already floating

        // Tell the terminal control not to kill the PTY on detach.
        if (state.Instance?.TabContent is TerminalControl tcOut)
            tcOut.BeginReparent();

        // Detach content from the TabItem so the host Panel can be re-parented.
        tab.Content = null;
        TerminalTabs.Items.Remove(tab);

        // If this session's SFTP panel was visible in the main window, replace it with
        // the placeholder so the left pane doesn't hold a stale reference.
        if (state.Instance?.SftpPanel != null &&
            SftpPanelHost?.Content == state.Instance.SftpPanel)
        {
            SftpPanelHost.Content = new TextBlock
            {
                Text = "No SFTP session active",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                FontSize = 11,
            };
            if (LeftTabs?.SelectedIndex == 1)
                LeftTabs.SelectedIndex = 0;
        }

        var win = new FloatingSessionWindow(
            state.Host,
            state.Instance?.SftpPanel,
            state.TitleBlock,
            () => DockBackSession(state),
            async () => await CloseTabAsync(state.Tab))
        {
            Width = Bounds.Width,
            Height = Bounds.Height,
        };

        state.FloatingWindow = win;
        win.Show();

        // EndReparent after the control has been re-attached to the new visual tree.
        if (state.Instance?.TabContent is TerminalControl tcEnd)
            Dispatcher.UIThread.Post(() => tcEnd.EndReparent(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Moves a floating session back into the main tab strip.
    /// Called from <see cref="FloatingSessionWindow"/> via a delegate.
    /// </summary>
    private void DockBackSession(TabState state)
    {
        var win = state.FloatingWindow;
        if (win == null) return;

        // Tell the terminal control not to kill the PTY on detach from the floating window.
        if (state.Instance?.TabContent is TerminalControl tcIn)
            tcIn.BeginReparent();

        // Detach hosted controls from the floating window before re-parenting.
        win.DetachContents();

        // Restore the Tab's content and add it back to the main strip.
        state.Tab.Content = state.Host;
        state.FloatingWindow = null;

        TerminalTabs.Items.Add(state.Tab);
        TerminalTabs.SelectedItem = state.Tab;

        // Close the floating window. _isDocking is already true so Closing won't
        // trigger a second dock-back.
        win.Close();

        // EndReparent after re-attachment.
        if (state.Instance?.TabContent is TerminalControl tcDone)
            Dispatcher.UIThread.Post(() => tcDone.EndReparent(), DispatcherPriority.Loaded);

        // OnTabSelectionChanged fires and restores the SFTP panel if applicable.
        // Also focus the terminal.
        if (state.Instance?.TabContent is { } tc)
            FocusTerminal(tc);
    }

    private static void FocusTerminal(Control terminal)
    {
        void OnLoaded(object? s, RoutedEventArgs _)
        {
            terminal.Loaded -= OnLoaded;
            terminal.Focus();
        }
        terminal.Loaded += OnLoaded;
        if (terminal.IsLoaded)
            terminal.Focus();
    }

    private void OnTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TerminalTabs is null) return;
        if (TerminalTabs.SelectedItem is not TabItem selected) return;

        _tabStates.TryGetValue(selected, out var selState);

        // Focus the active terminal (skip when a disconnect overlay is showing)
        if (selState?.Overlay is null && selState?.Instance?.TabContent is { } term)
        {
            if (term.IsLoaded)
                Dispatcher.UIThread.Post(() => term.Focus(), DispatcherPriority.Background);
        }

        // Update SFTP panel
        if (SftpPanelHost is null) return;
        if (selState?.Overlay is null && selState?.Instance?.SftpPanel is { } panel)
        {
            SftpPanelHost.Content = panel;
            LeftTabs.SelectedIndex = 1; // Switch to SFTP tab
        }
        else
        {
            SftpPanelHost.Content = new TextBlock
            {
                Text = "No SFTP session active",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                FontSize = 11,
            };
        }
    }

    // -----------------------------------------------------------------------
    // Config
    // -----------------------------------------------------------------------

    private void ClearSftpPanelIfNeeded(TabItem closedTab)
    {
        // If the SFTP panel is showing content for the closed tab, replace it with the placeholder.
        if (SftpPanelHost is null) return;
        // After removal the tab is no longer selected; check if no remaining active instance has a panel.
        bool anyHasPanel = _tabStates.Values.Any(s => s.Instance?.SftpPanel != null);
        if (!anyHasPanel)
        {
            SftpPanelHost.Content = new TextBlock
            {
                Text = "No SFTP session active",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                FontSize = 11,
            };
            // Switch left panel back to Sessions if SFTP tab was active
            if (LeftTabs?.SelectedIndex == 1)
                LeftTabs.SelectedIndex = 0;
        }
    }

    private void SaveConfig() => ConfigStore.Save(_config);

    // -----------------------------------------------------------------------
    // Window lifecycle
    // -----------------------------------------------------------------------

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        foreach (var state in _tabStates.Values)
        {
            try { state.Instance?.Kill(); } catch { }
        }
        _tabStates.Clear();

        // Persist window size/state and left-tab selection
        var isMaximized = WindowState == WindowState.Maximized;
        SettingsService.Temp.WindowMaximized = isMaximized;
        // Only save Width/Height when not maximized — keep the restored size
        if (!isMaximized)
        {
            SettingsService.Temp.WindowWidth  = Width;
            SettingsService.Temp.WindowHeight = Height;
        }
        if (LeftTabs != null)
            SettingsService.Temp.ActiveLeftTab = LeftTabs.SelectedIndex;
        if (_mainBodyGrid != null)
            SettingsService.Temp.LeftColumnWidthPx = _mainBodyGrid.ColumnDefinitions[0].ActualWidth;
        SettingsService.SaveTemp();

        SaveConfig();
        base.OnClosing(e);
        Environment.Exit(0);
    }

    // -----------------------------------------------------------------------
    // Utilities
    // -----------------------------------------------------------------------

    private Task ShowErrorAsync(string title, string message)
    {
        var win = new Window
        {
            Title = title,
            Width = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right },
                }
            },
        };
        var ok = (Button)((StackPanel)win.Content).Children[1];
        ok.Click += (_, _) => win.Close();
        return win.ShowDialog(this);
    }

    /// <summary>Finds a saved <see cref="SessionDefinition"/> by id, or null if not found.</summary>
    private static SessionDefinition? FindSessionById(Guid id, SessionGroup group)
    {
        foreach (var s in group.Sessions)
            if (s.Id == id) return s;
        foreach (var g in group.Subgroups)
        {
            var found = FindSessionById(id, g);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Collects all session IDs in the tree (for recent-sessions pruning).</summary>
    private static HashSet<Guid> CollectAllSessionIds(SessionGroup group)
    {
        var set = new HashSet<Guid>();
        void Walk(SessionGroup g)
        {
            foreach (var s in g.Sessions) set.Add(s.Id);
            foreach (var sub in g.Subgroups) Walk(sub);
        }
        Walk(group);
        return set;
    }

    private static List<string> ParseCommandLine(string commandLine)
    {
        var args = new List<string>();
        var current = string.Empty;
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; }
            else if (c == ' ' && !inQuotes)
            {
                if (!string.IsNullOrEmpty(current)) { args.Add(current); current = string.Empty; }
            }
            else { current += c; }
        }

        if (!string.IsNullOrEmpty(current)) args.Add(current);
        return args;
    }
}
