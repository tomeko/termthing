using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Renci.SshNet;
using TermThing.Panes;
using TermThing.Views;

namespace TermThing.Tmux;

/// <summary>
/// One tmux session shown natively through control mode: each tmux window is a
/// <see cref="PaneLayoutView"/> (picked from a strip of sub-tabs) and each tmux pane a
/// <see cref="TerminalControl"/> fed by its <see cref="TmuxPaneConnection"/>.
/// <para>
/// tmux is the source of truth. Layouts come from <c>%layout-change</c> and from
/// <c>list-windows</c> (run again whenever windows come or go); the view never changes
/// them itself. The size of the whole client is told to tmux with <c>refresh-client -C</c>.
/// </para>
/// <para>
/// What the user does turns into tmux commands: keys and pastes into <c>send-keys</c>
/// (minus the terminal's own replies to queries, which tmux answers itself), pane focus
/// into <c>select-pane</c>, and splits, closes, resizes and zoom into the matching tmux
/// commands. The view then follows tmux's notifications.
/// </para>
/// </summary>
public sealed class TmuxControlSession : IDisposable
{
    private static readonly IBrush StripBrush = new SolidColorBrush(Color.Parse("#252526"));
    private static readonly IBrush SelectedTabBrush = new SolidColorBrush(Color.Parse("#094771"));
    private static readonly IBrush HoverTabBrush = new SolidColorBrush(Color.Parse("#2a2d2e"));
    private static readonly Size FallbackCell = new(8, 17);

    private readonly TmuxControlChannel _channel;
    private readonly Func<TerminalControl> _createTerminal;

    // Pane output is fed from the reader thread, possibly before the pane's terminal exists.
    private readonly ConcurrentDictionary<int, TmuxPaneConnection> _connections = new();
    private readonly ConcurrentDictionary<int, byte> _closedPanes = new();

    // UI thread only.
    private readonly Dictionary<int, TmuxWindow> _windows = new();
    private readonly Dictionary<int, TerminalControl> _terminals = new();
    private readonly Grid _root;
    private readonly StackPanel _strip;
    private readonly TextBlock _sessionLabel;
    private readonly Panel _host;
    private readonly DispatcherTimer _sizeTimer;
    private int? _currentWindowId;
    private (int Cols, int Rows) _sentSize;
    private bool _syncRunning, _syncAgain;
    private bool _remote;   // applying a change that came from tmux: don't echo it back
    private bool _disposed;

    private sealed class TmuxWindow
    {
        public required int Id { get; init; }
        public required PaneLayoutView View { get; init; }
        public required Border Tab { get; init; }
        public required TextBlock Label { get; init; }
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public TmuxControlSession(SshClient client, string sessionName, Func<TerminalControl> createTerminal)
    {
        SessionName = sessionName;
        _createTerminal = createTerminal;
        _channel = TmuxControlChannel.AttachOrCreate(client, sessionName);
        _channel.Output += OnOutput;
        _channel.Notification += n => Dispatcher.UIThread.Post(() => OnNotification(n));
        _channel.Closed += reason => Dispatcher.UIThread.Post(() => OnClosed(reason));

        _sessionLabel = new TextBlock
        {
            Text = "tmux: " + sessionName,
            Foreground = Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 10, 0),
        };
        var newWindow = new TextBlock
        {
            Text = "+",
            FontSize = 13,
            Foreground = Brushes.Gray,
            Padding = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(newWindow, "New tmux window");
        newWindow.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(newWindow).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            NewWindow();
        };
        _strip = new StackPanel { Orientation = Orientation.Horizontal, Children = { _sessionLabel, newWindow } };
        _host = new Panel { Background = Brushes.Black, ClipToBounds = true };
        _host.SizeChanged += (_, _) => ScheduleClientSize();

        _root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var stripBorder = new Border { Background = StripBrush, Height = 22, Child = _strip };
        Grid.SetRow(stripBorder, 0);
        Grid.SetRow(_host, 1);
        _root.Children.Add(stripBorder);
        _root.Children.Add(_host);

        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _sizeTimer.Tick += (_, _) => { _sizeTimer.Stop(); SendClientSize(); };
    }

    /// <summary>The tmux session shown (follows renames and session switches).</summary>
    public string SessionName { get; private set; }

    /// <summary>The control to place in the tab.</summary>
    public Control View => _root;

    /// <summary>The panes of the tmux window on show.</summary>
    public PaneLayoutView? ActivePanes =>
        _currentWindowId is { } id && _windows.TryGetValue(id, out var w) ? w.View : null;

    /// <summary>The active pane of the tmux window on show.</summary>
    public TerminalControl? ActiveTerminal => ActivePanes?.ActivePane as TerminalControl;

    /// <summary>Every pane's terminal, in all windows.</summary>
    public IReadOnlyList<TerminalControl> Terminals => [.. _terminals.Values];

    /// <summary>Raised (UI thread) when the window on show or its active pane changes.</summary>
    public event EventHandler? ActivePaneChanged;

    /// <summary>Raised (UI thread) for every pane terminal created, e.g. to watch its title.</summary>
    public event Action<TerminalControl>? TerminalAdded;

    /// <summary>Raised (UI thread) when a pane is gone; its terminal is already out of the view.</summary>
    public event Action<TerminalControl>? TerminalRemoved;

    /// <summary>Raised once (UI thread) when the control client has ended, with tmux's reason if any.</summary>
    public event Action<string?>? Ended;

    /// <summary>The working folder of the active pane (tmux's <c>#{pane_current_path}</c>), once known.</summary>
    public string? ActiveDirectory { get; private set; }

    /// <summary>Raised (UI thread) when <see cref="ActiveDirectory"/> changes: a <c>cd</c>, or another pane or window.</summary>
    public event Action<string>? ActiveDirectoryChanged;

    /// <summary>Types <paramref name="text"/> into the active pane, as if from the keyboard.</summary>
    public void SendToActivePane(string text)
    {
        if (ActivePanes is { } view && view.PaneIdOf(view.ActivePane) is { } id)
            foreach (var command in TmuxInput.SendKeys(id, System.Text.Encoding.UTF8.GetBytes(text)))
                Post(command);
    }

    /// <summary>
    /// Starts <c>tmux -C</c> and loads the session's windows and panes. Throws, with
    /// tmux's reason, when control mode didn't come up (e.g. tmux isn't installed).
    /// </summary>
    public async Task StartAsync()
    {
        await Task.Run(_channel.Start);
        ScheduleClientSize();
        // tmux re-checks subscriptions about once a second and reports changes; with no
        // target, the format follows the session's active pane.
        Post("refresh-client -B 'cwd::#{pane_current_path}'");
        await SyncAsync();
        if (_channel.IsClosed || _windows.Count == 0)
            throw new InvalidOperationException(
                "tmux control mode could not start: " + (_channel.CloseReason ?? "no windows were reported."));
    }

    // -----------------------------------------------------------------------
    // tmux → view
    // -----------------------------------------------------------------------

    /// <summary>Reader thread: hand pane output straight to its connection.</summary>
    private void OnOutput(int paneId, byte[] data)
    {
        if (_closedPanes.ContainsKey(paneId)) return;
        _connections.GetOrAdd(paneId, NewConnection).Feed(data);
    }

    private TmuxPaneConnection NewConnection(int paneId) => new(paneId, OnPaneInput);

    /// <summary>
    /// What a pane's terminal sends (on a terminal write thread): keys, pastes and
    /// mouse reports go to tmux; the terminal's own replies to queries don't.
    /// </summary>
    private void OnPaneInput(int paneId, byte[] data)
    {
        if (_disposed || _channel.IsClosed || TmuxInput.IsTerminalReply(data)) return;
        foreach (var command in TmuxInput.SendKeys(paneId, data))
            _channel.Post(command);
    }

    private void OnNotification(TmuxNotification n)
    {
        if (_disposed) return;
        switch (n.Name)
        {
            case "layout-change":
                // %layout-change @win layout visible-layout flags. The layout has every pane;
                // the visible one is what is on screen (just the zoomed pane when zoomed).
                if (TmuxControlProtocol.TryParseId(n.Arg(0), out var winId) && _windows.TryGetValue(winId, out var window))
                {
                    if (!ApplyLayout(window, n.Arg(1), n.Arg(3).Contains('Z') ? n.Arg(2) : null)) RequestSync();
                }
                else RequestSync();
                break;

            case "window-pane-changed":
                if (TmuxControlProtocol.TryParseId(n.Arg(0), out var w) && _windows.TryGetValue(w, out var win)
                    && TmuxControlProtocol.TryParseId(n.Arg(1), out var pane))
                    Remote(() => win.View.ActivatePane(pane, focus: w == _currentWindowId && _root.IsKeyboardFocusWithin));
                break;

            case "session-window-changed":
                if (TmuxControlProtocol.TryParseId(n.Arg(1), out var shown)) ShowWindow(shown);
                break;

            case "session-changed":
                SessionName = n.RestAfter(1);
                _sessionLabel.Text = "tmux: " + SessionName;
                RequestSync();
                break;

            case "session-renamed":
                // %session-renamed $id name (tmux 3.x); only ours matters to us, and we
                // can't tell ids apart cheaply, so re-read our own name.
                RequestSync();
                break;

            case "subscription-changed":
                // %subscription-changed name $session @window index %pane … : value
                if (n.Arg(0) == "cwd" && n.Rest.IndexOf(" : ", StringComparison.Ordinal) is var at and >= 0)
                {
                    var dir = n.Rest[(at + 3)..];
                    if (dir.Length > 0 && dir != ActiveDirectory)
                    {
                        ActiveDirectory = dir;
                        ActiveDirectoryChanged?.Invoke(dir);
                    }
                }
                break;

            case "window-add":
            case "window-close":
            case "unlinked-window-close":
            case "window-renamed":
                RequestSync();
                break;
        }
    }

    private void OnClosed(string? reason)
    {
        if (_disposed) return;
        foreach (var c in _connections.Values) c.Close();
        Ended?.Invoke(reason);
    }

    /// <summary>
    /// Re-reads windows and panes from tmux and makes the view match. Calls that arrive
    /// while one is running are folded into one more run.
    /// </summary>
    private void RequestSync()
    {
        if (_syncRunning) { _syncAgain = true; return; }
        _ = SyncAsync();
    }

    private async Task SyncAsync()
    {
        if (_disposed || _channel.IsClosed) return;
        _syncRunning = true;
        try
        {
            do
            {
                _syncAgain = false;
                // Name last: it may contain spaces.
                var windows = await _channel.SendAsync(
                    "list-windows -F '#{window_id} #{window_index} #{window_active} #{window_zoomed_flag} " +
                    "#{window_layout} #{window_visible_layout} #{window_name}'");
                var panes = await _channel.SendAsync(
                    "list-panes -s -F '#{window_id} #{pane_id} #{pane_active}'");
                var session = await _channel.SendAsync("display-message -p '#{session_name}'");
                if (_disposed) return;
                if (session.Success && session.Lines.Count > 0)
                {
                    SessionName = session.Lines[0];
                    _sessionLabel.Text = "tmux: " + SessionName;
                }
                if (windows.Success) Reconcile(windows.Lines, panes.Success ? panes.Lines : []);
            }
            while (_syncAgain && !_disposed);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[tmux] sync failed: {ex.Message}");
        }
        finally
        {
            _syncRunning = false;
        }
    }

    private void Reconcile(IReadOnlyList<string> windowLines, IReadOnlyList<string> paneLines)
    {
        var seen = new HashSet<int>();
        int? activeWindow = null;
        foreach (var line in windowLines)
        {
            var p = line.Split(' ', 7);
            if (p.Length < 6 || !TmuxControlProtocol.TryParseId(p[0], out var id)) continue;
            int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index);
            var name = p.Length > 6 ? p[6] : string.Empty;
            var zoomedLayout = p[3] == "1" ? p[5] : null;

            if (!_windows.TryGetValue(id, out var window))
            {
                window = CreateWindow(id, p[4]);
                if (window is null) continue;
            }
            ApplyLayout(window, p[4], zoomedLayout);

            window.Index = index;
            window.Name = name;
            window.Label.Text = $"{index}:{name}";
            seen.Add(id);
            if (p[2] == "1") activeWindow = id;
        }

        foreach (var gone in _windows.Keys.Where(id => !seen.Contains(id)).ToList())
            RemoveWindow(gone);

        // Order the sub-tabs by tmux's window index.
        var ordered = _windows.Values.OrderBy(w => w.Index).Select(w => (Control)w.Tab).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int at = _strip.Children.IndexOf(ordered[i]);
            if (at != i + 1) { _strip.Children.Remove(ordered[i]); _strip.Children.Insert(i + 1, ordered[i]); }
        }

        foreach (var line in paneLines)
        {
            var p = line.Split(' ');
            if (p.Length < 3 || p[2] != "1") continue;
            if (TmuxControlProtocol.TryParseId(p[0], out var wid) && _windows.TryGetValue(wid, out var w)
                && TmuxControlProtocol.TryParseId(p[1], out var pid))
                Remote(() => w.View.ActivatePane(pid, focus: false));
        }

        if (activeWindow is { } a) ShowWindow(a);
        else if (_currentWindowId is null && _windows.Count > 0) ShowWindow(_windows.Keys.First());
    }

    // -----------------------------------------------------------------------
    // Windows
    // -----------------------------------------------------------------------

    private TmuxWindow? CreateWindow(int id, string layoutString)
    {
        PaneLayout layout;
        try { layout = PaneLayout.ParseTmux(layoutString); }
        catch (FormatException ex)
        {
            Debug.WriteLine($"[tmux] bad layout for @{id}: {ex.Message} ({layoutString})");
            return null;
        }

        var view = new PaneLayoutView(layout, CreatePane)
        {
            CellSizeProvider = CellSize,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        view.SplitRequested = axis =>
            Post($"split-window {(axis == SplitAxis.LeftRight ? "-h" : "-v")} -t %{ActiveId(view)} -c '#{{pane_current_path}}'");
        view.CloseRequested = pane => { if (view.PaneIdOf(pane) is { } p) Post($"kill-pane -t %{p}"); };
        view.ResizeRequested = (p, direction, cells) =>
            Post($"resize-pane -t %{p} -{DirectionFlag(direction)} {cells}");
        view.DividerMoveRequested = (split, index, size) =>
            Post($"resize-pane -t %{split.Children[index].Leaves().First().PaneId} -{(split.Axis == SplitAxis.LeftRight ? 'x' : 'y')} {size}");
        view.EqualizeRequested = split => Post($"select-layout -E -t %{split.Leaves().First().PaneId}");
        view.ZoomRequested = p => Post($"resize-pane -Z -t %{p}");
        view.ActivePaneChanged += (_, _) =>
        {
            // The user picked a pane (click, focus, Alt+Arrow): tell tmux.
            if (!_remote) Post($"select-pane -t %{ActiveId(view)}");
            if (_currentWindowId == id) ActivePaneChanged?.Invoke(this, EventArgs.Empty);
        };

        var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gainsboro };
        var tab = new Border
        {
            Child = label,
            Padding = new Thickness(10, 0),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        tab.PointerEntered += (_, _) => { if (_currentWindowId != id) tab.Background = HoverTabBrush; };
        tab.PointerExited += (_, _) => { if (_currentWindowId != id) tab.Background = Brushes.Transparent; };
        tab.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(tab).Properties;
            if (point.IsMiddleButtonPressed) { e.Handled = true; Post($"kill-window -t @{id}"); return; }
            if (!point.IsLeftButtonPressed) return;
            e.Handled = true;
            ShowWindow(id);
            Post($"select-window -t @{id}");
            if (ActiveTerminal is { } t) Dispatcher.UIThread.Post(() => t.Focus(), DispatcherPriority.Input);
        };
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var newItem = new MenuItem { Header = "New Window" };
            var rename = new MenuItem { Header = "Rename Window…" };
            var close = new MenuItem { Header = "Close Window" };
            newItem.Click += (_, _) => NewWindow();
            rename.Click += async (_, _) => await RenameWindowAsync(id);
            close.Click += (_, _) => Post($"kill-window -t @{id}");
            menu.Items.Add(newItem);
            menu.Items.Add(rename);
            menu.Items.Add(new Separator());
            menu.Items.Add(close);
        };
        tab.ContextMenu = menu;
        ToolTip.SetTip(tab, "Middle-click to close · right-click for more");

        var window = new TmuxWindow { Id = id, View = view, Tab = tab, Label = label };
        _windows[id] = window;
        // Every window stays in the tree and laid out (a hidden window's terminals must
        // keep processing output at their real size); only the one on show is visible.
        _host.Children.Add(view);
        _strip.Children.Insert(_strip.Children.Count - 1, tab);   // before the "+"
        return window;
    }

    /// <summary>
    /// Shows <paramref name="layoutString"/> (every pane) in the window, zoomed to the one
    /// pane of <paramref name="zoomedLayout"/> when tmux has a pane zoomed.
    /// </summary>
    private bool ApplyLayout(TmuxWindow window, string layoutString, string? zoomedLayout)
    {
        PaneLayout layout;
        try { layout = PaneLayout.ParseTmux(layoutString); }
        catch (FormatException ex)
        {
            Debug.WriteLine($"[tmux] bad layout for @{window.Id}: {ex.Message} ({layoutString})");
            return false;
        }
        int? zoomed = null;
        if (zoomedLayout is not null)
        {
            try { if (PaneLayout.ParseTmux(zoomedLayout).Root is { IsLeaf: true } leaf) zoomed = leaf.PaneId; }
            catch (FormatException) { }
        }
        Remote(() =>
        {
            foreach (var removed in window.View.ApplyLayout(layout, CreatePane))
                if (removed is TerminalControl tc) PaneGone(tc);
            window.View.SetZoomedPane(zoomed);
        });
        return true;
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    private void Post(string command)
    {
        if (!_disposed && !_channel.IsClosed) _channel.Post(command);
    }

    private static int ActiveId(PaneLayoutView view) => view.PaneIdOf(view.ActivePane) ?? -1;

    private static char DirectionFlag(PaneDirection d) => d switch
    {
        PaneDirection.Left => 'L',
        PaneDirection.Right => 'R',
        PaneDirection.Up => 'U',
        _ => 'D',
    };

    /// <summary>Runs a view change that mirrors tmux, so it isn't sent back as a command.</summary>
    private void Remote(Action apply)
    {
        bool was = _remote;
        _remote = true;
        try { apply(); }
        finally { _remote = was; }
    }

    /// <summary>A new tmux window after the one on show, in its active pane's folder.</summary>
    private void NewWindow() =>
        Post(_currentWindowId is { } w
            ? $"new-window -a -t @{w} -c '#{{pane_current_path}}'"
            : "new-window -c '#{pane_current_path}'");

    private async Task RenameWindowAsync(int id)
    {
        if (!_windows.TryGetValue(id, out var window) || TopLevel.GetTopLevel(_root) is not Window owner) return;
        var dialog = new RenameDialog(window.Name) { Title = "Rename tmux Window" };
        var name = await dialog.ShowDialog<string?>(owner);
        if (!string.IsNullOrWhiteSpace(name))
            Post($"rename-window -t @{id} {TmuxControlProtocol.Quote(name.Trim())}");
        if (ActiveTerminal is { } t) t.Focus();
    }

    private void RemoveWindow(int id)
    {
        if (!_windows.Remove(id, out var window)) return;
        _host.Children.Remove(window.View);
        _strip.Children.Remove(window.Tab);
        foreach (var pane in window.View.Panes.OfType<TerminalControl>().ToList())
            PaneGone(pane);
        if (_currentWindowId == id)
        {
            _currentWindowId = null;
            if (_windows.Count > 0) ShowWindow(_windows.Values.OrderBy(w => w.Index).First().Id);
        }
    }

    private void ShowWindow(int id)
    {
        if (!_windows.ContainsKey(id) || _currentWindowId == id) return;
        bool hadFocus = _root.IsKeyboardFocusWithin;
        _currentWindowId = id;
        foreach (var (wid, w) in _windows)
        {
            bool on = wid == id;
            w.View.Opacity = on ? 1 : 0;
            w.View.IsHitTestVisible = on;
            w.View.ZIndex = on ? 1 : 0;
            w.Tab.Background = on ? SelectedTabBrush : Brushes.Transparent;
        }
        if (hadFocus && ActiveTerminal is { } t) Dispatcher.UIThread.Post(() => t.Focus(), DispatcherPriority.Input);
        ActivePaneChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------
    // Panes
    // -----------------------------------------------------------------------

    private Control CreatePane(int paneId)
    {
        _closedPanes.TryRemove(paneId, out _);
        var connection = _connections.GetOrAdd(paneId, NewConnection);
        var tc = _createTerminal();

        // The scrollbar column would cost the terminal a column or two, and tmux's pane
        // width must match the terminal's exactly.
        tc.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<ScrollBar>("PART_ScrollBar") is { } bar) bar.IsVisible = false;
        };

        void Attach()
        {
            if (connection.IsClosed) return;
            tc.AttachConnection(connection);
            ScheduleClientSize();   // the first loaded terminal gives the real cell size
        }
        if (tc.IsLoaded) Attach();
        else
        {
            void OnLoaded(object? s, Avalonia.Interactivity.RoutedEventArgs e) { tc.Loaded -= OnLoaded; Attach(); }
            tc.Loaded += OnLoaded;
        }

        _terminals[paneId] = tc;
        TerminalAdded?.Invoke(tc);
        return tc;
    }

    /// <summary>A pane is gone from tmux: end its stream and forget it.</summary>
    private void PaneGone(TerminalControl tc)
    {
        var entry = _terminals.FirstOrDefault(kv => ReferenceEquals(kv.Value, tc));
        if (entry.Value is null) return;
        _terminals.Remove(entry.Key);
        _closedPanes[entry.Key] = 0;
        if (_connections.TryRemove(entry.Key, out var connection)) connection.Close();
        TerminalRemoved?.Invoke(tc);
    }

    // -----------------------------------------------------------------------
    // Size
    // -----------------------------------------------------------------------

    private Size CellSize()
    {
        foreach (var tc in _terminals.Values)
            if (tc.CharWidth > 0 && tc.CharHeight > 0) return new Size(tc.CharWidth, tc.CharHeight);
        return FallbackCell;
    }

    private void ScheduleClientSize()
    {
        if (_disposed) return;
        _sizeTimer.Stop();
        _sizeTimer.Start();
    }

    /// <summary>
    /// Tells tmux how many cells the tab has room for. Half a cell is kept back, matching
    /// the slack <see cref="PaneLayoutView"/> leaves for pixel rounding.
    /// </summary>
    private void SendClientSize()
    {
        if (_disposed || _channel.IsClosed || _host.Bounds.Width <= 0 || _host.Bounds.Height <= 0) return;
        var cell = CellSize();
        int cols = Math.Max(10, (int)Math.Floor((_host.Bounds.Width - cell.Width / 2) / cell.Width));
        int rows = Math.Max(4, (int)Math.Floor((_host.Bounds.Height - cell.Height / 2) / cell.Height));
        if ((cols, rows) == _sentSize) return;
        _sentSize = (cols, rows);
        _channel.Post(string.Create(CultureInfo.InvariantCulture, $"refresh-client -C {cols}x{rows}"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sizeTimer.Stop();
        try { _channel.Dispose(); } catch { }
        foreach (var c in _connections.Values) c.Close();
        _connections.Clear();
    }
}
