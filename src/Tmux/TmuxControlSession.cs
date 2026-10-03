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
using TermThing.Diagnostics;

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
    private static readonly IBrush SizeHintBrush = new SolidColorBrush(Color.Parse("#d7ba7d"));
    private static readonly Size FallbackCell = new(8, 17);
    private static readonly TimeSpan ReclaimInterval = TimeSpan.FromSeconds(2);

    // Flow control. tmux pauses a pane whose output is this many seconds behind (its side
    // of the connection is backed up); we pause one whose terminal has this much unread
    // (our side is). Either way the pane is redrawn from a capture once it can continue.
    private const int PauseAfterSeconds = 10;
    private const long MaxPendingBytes = 4 * 1024 * 1024;

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
    private readonly DispatcherTimer _sizeTimer, _sizeHintTimer;
    private int? _currentWindowId;
    private (int Cols, int Rows) _sentSize;
    private bool _syncRunning, _syncAgain;
    private bool _syncingFont;
    private (FontFamily Family, double Size, FontStyle Style, FontWeight Weight)? _cellFont;
    private Size _cell;

    // The window on show is sized for another client (-1: no). Read on terminal write threads.
    private volatile int _contestedWindowId = -1;

    // Panes whose connection is made while this is set hold their output until their
    // existing content has been captured (see RestorePanes). Set from the start and on a
    // session switch (by the reader thread, before any of the new session's output);
    // cleared once a sync has seen that session's panes.
    private readonly object _holdLock = new();
    private volatile bool _holdNewPanes = true;
    private int _holdGeneration;
    private readonly HashSet<int> _restoring = new();   // UI thread only

    // Panes paused, or with a pause asked for. Added to on the reader thread.
    private readonly ConcurrentDictionary<int, byte> _paused = new();

    // Panes in a tmux mode (copy mode, usually entered from another client). Read on
    // terminal write threads.
    private readonly ConcurrentDictionary<int, byte> _inMode = new();

    private long _lastReclaim;
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
        _sessionName = sessionName;
        _createTerminal = createTerminal;
        _channel = TmuxControlChannel.AttachOrCreate(client, sessionName);
        _channel.Output += OnOutput;
        _channel.Notification += n =>
        {
            if (n.Name == "session-changed")
            {
                lock (_holdLock)
                {
                    _holdGeneration++;
                    _holdNewPanes = true;
                }
            }
            Dispatcher.UIThread.Post(() => OnNotification(n));
        };
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
        _root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _sizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _sizeTimer.Tick += (_, _) => { _sizeTimer.Stop(); SendClientSize(); };
        // After a resize, give tmux time to answer with %layout-change before calling the
        // window "sized by another client".
        _sizeHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sizeHintTimer.Tick += (_, _) => { _sizeHintTimer.Stop(); UpdateSessionLabel(); };
    }

    /// <summary>The tmux session shown (follows renames and session switches).</summary>
    public string SessionName
    {
        get => _sessionName;
        private set
        {
            if (_sessionName == value) return;
            _sessionName = value;
            SessionNameChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    private string _sessionName;

    /// <summary>Raised (UI thread) when <see cref="SessionName"/> changes: a rename, or a switch to another session.</summary>
    public event EventHandler? SessionNameChanged;

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

    /// <summary>
    /// Raised once (UI thread) when the control client has ended, with tmux's reason if any.
    /// Not raised for <see cref="Dispose"/>.
    /// </summary>
    public event Action<string?>? Ended;

    /// <summary>Raised (UI thread) with something worth telling the user, e.g. an error in the tmux config.</summary>
    public event Action<string>? Message;

    /// <summary>
    /// True when tmux itself ended the client (<c>%exit</c>: detached, session killed, server
    /// gone); false when the channel failed, e.g. the connection dropped.
    /// </summary>
    public bool EndedByTmux => _channel.ExitReceived;

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
        // From here on, output comes as %extended-output and a pane that falls behind is
        // paused (%pause) rather than queued without bound on the host.
        Post(string.Create(CultureInfo.InvariantCulture, $"refresh-client -f pause-after={PauseAfterSeconds}"));
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
        var connection = _connections.GetOrAdd(paneId, NewConnection);
        connection.Feed(data);
        // The terminal can't keep up (a flood of output): stop the pane rather than let the
        // queue, and the delay before a Ctrl+C shows, grow without bound.
        if (connection.PendingBytes > MaxPendingBytes && _paused.TryAdd(paneId, 0))
            _channel.Post($"refresh-client -A '%{paneId}:pause'");
    }

    private TmuxPaneConnection NewConnection(int paneId) => new(paneId, OnPaneInput, hold: _holdNewPanes);

    /// <summary>
    /// What a pane's terminal sends (on a terminal write thread): keys, pastes and
    /// mouse reports go to tmux; the terminal's own replies to queries don't.
    /// </summary>
    private void OnPaneInput(int paneId, byte[] data)
    {
        if (_disposed || _channel.IsClosed || TmuxInput.IsTerminalReply(data)) return;
        ReclaimSize();
        LeaveMode(paneId);
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
                UpdateSessionLabel();
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

            case "pause":
                // %pause %pane: tmux stopped sending its output (it fell behind, or we
                // asked). Continue once its terminal has read what it has.
                if (TmuxControlProtocol.TryParseId(n.Arg(0), out var paused) && !_closedPanes.ContainsKey(paused))
                {
                    _paused.TryAdd(paused, 0);
                    _connections.GetOrAdd(paused, NewConnection).WhenDrained(() => Dispatcher.UIThread.Post(() => ResumePane(paused)));
                }
                break;

            case "pane-mode-changed":
                // %pane-mode-changed %pane: it entered or left a mode (copy mode, a
                // chooser…), which another client can do; ask which.
                if (TmuxControlProtocol.TryParseId(n.Arg(0), out var modePane)) _ = CheckModeAsync(modePane);
                break;

            case "config-error":
                Message?.Invoke("tmux config error: " + n.Rest);
                break;

            case "continue":
                if (TmuxControlProtocol.TryParseId(n.Arg(0), out var continued)) _paused.TryRemove(continued, out _);
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
                int holdGeneration;
                lock (_holdLock) holdGeneration = _holdGeneration;
                // Name last: it may contain spaces.
                var windows = await _channel.SendAsync(
                    "list-windows -F '#{window_id} #{window_index} #{window_active} #{window_zoomed_flag} " +
                    "#{window_layout} #{window_visible_layout} #{window_name}'");
                var panes = await _channel.SendAsync(
                    "list-panes -s -F '#{window_id} #{pane_id} #{pane_active} #{pane_in_mode}'");
                var session = await _channel.SendAsync("display-message -p '#{session_name}'");
                if (_disposed) return;
                if (session.Success && session.Lines.Count > 0)
                {
                    SessionName = session.Lines[0];
                    UpdateSessionLabel();
                }
                if (windows.Success) Reconcile(windows.Lines, panes.Success ? panes.Lines : []);
                if (windows.Success && panes.Success) RestorePanes(holdGeneration);
            }
            while (_syncAgain && !_disposed);
        }
        catch (Exception ex)
        {
            Log.Warn("tmux", $"sync failed: {ex.Message}");
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
            // A pane already in a mode when we attached sends no %pane-mode-changed.
            if (p.Length > 3 && TmuxControlProtocol.TryParseId(p[1], out var modePane))
            {
                if (p[3] == "1") _inMode[modePane] = 0;
                else _inMode.TryRemove(modePane, out _);
            }
            if (p.Length < 3 || p[2] != "1") continue;
            if (TmuxControlProtocol.TryParseId(p[0], out var wid) && _windows.TryGetValue(wid, out var w)
                && TmuxControlProtocol.TryParseId(p[1], out var pid))
                Remote(() => w.View.ActivatePane(pid, focus: false));
        }

        if (activeWindow is { } a) ShowWindow(a);
        else if (_currentWindowId is null && _windows.Count > 0) ShowWindow(_windows.Keys.First());
    }

    /// <summary>
    /// After a sync: fills each held pane that the view now has with what it already
    /// shows, then lets its live output through. Once a sync has seen the panes of the
    /// session (no switch since it started), new panes stop being held: splits and new
    /// windows start empty, and panes in hidden windows have been read all along.
    /// </summary>
    private void RestorePanes(int holdGeneration)
    {
        bool sized = false;
        foreach (var (paneId, connection) in _connections)
        {
            if (!connection.IsHeld || _restoring.Contains(paneId)) continue;
            if (_terminals.TryGetValue(paneId, out var tc))
            {
                if (!sized)
                {
                    // Capture at the size the tab will have, not the size tmux had: tmux
                    // runs commands in order, so this resize happens before the captures.
                    _sizeTimer.Stop();
                    SendClientSize();
                    ScheduleClientSize();   // in case the tab had no size yet (repeats aren't sent)
                    sized = true;
                }
                _ = RestorePaneAsync(paneId, connection, tc.MaxScrollback);
            }
            else if (Volatile.Read(ref _holdGeneration) == holdGeneration)
            {
                connection.Release();   // not a pane of this session (any more)
            }
        }
        lock (_holdLock)
        {
            if (_holdGeneration == holdGeneration) _holdNewPanes = false;
        }
    }

    private async Task CheckModeAsync(int paneId)
    {
        try
        {
            var reply = await _channel.SendAsync($"display-message -p -t %{paneId} '#{{pane_in_mode}}'");
            if (reply.Success && reply.Lines.Count > 0 && reply.Lines[0] == "1") _inMode[paneId] = 0;
            else _inMode.TryRemove(paneId, out _);
        }
        catch (Exception ex)
        {
            Log.Warn("tmux", $"mode check for %{paneId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Terminal write thread, before the user's keys reach a pane: in a tmux mode (copy
    /// mode from another client, which this tab can't show), keys would drive the mode
    /// instead of the program, so leave it first, as typing into it in that client would.
    /// </summary>
    private void LeaveMode(int paneId)
    {
        if (_inMode.TryRemove(paneId, out _)) _channel.Post($"copy-mode -q -t %{paneId}");
    }

    /// <summary>
    /// A paused pane's terminal has caught up: let tmux send its output again, and
    /// redraw it, since the output made while paused is gone.
    /// </summary>
    private void ResumePane(int paneId)
    {
        if (_disposed || _channel.IsClosed || !_paused.ContainsKey(paneId)
            || !_connections.TryGetValue(paneId, out var connection) || connection.IsClosed)
        {
            _paused.TryRemove(paneId, out _);
            return;
        }
        if (_restoring.Contains(paneId))
        {
            // Still being filled in after attach; its output is held, so it looks drained.
            DispatcherTimer.RunOnce(() => ResumePane(paneId), TimeSpan.FromMilliseconds(250));
            return;
        }
        // Hold first, so live output that follows the continue lands after the redraw.
        connection.Hold();
        Post($"refresh-client -A '%{paneId}:continue'");
        _ = RestorePaneAsync(paneId, connection, maxScrollback: 0, redraw: true);
    }

    private async Task RestorePaneAsync(int paneId, TmuxPaneConnection connection, int maxScrollback, bool redraw = false)
    {
        _restoring.Add(paneId);
        byte[]? content = null;
        try
        {
            // Sent back to back, so tmux answers them together.
            var replies = await Task.WhenAll(TmuxPaneRestore.Commands(paneId, maxScrollback).Select(_channel.SendAsync).ToList());
            if (replies[0].Success && replies[0].Lines.Count > 0 && TmuxPaneRestore.Parse(replies[0].Lines[0]) is { } restore)
            {
                IReadOnlyList<string> Lines(TmuxReply r) => r.Success ? r.Lines : [];
                content = restore.Build(Lines(replies[1]), Lines(replies[2]), Lines(replies[3]), redraw);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("tmux", $"restoring %{paneId} failed: {ex.Message}");
        }
        finally
        {
            // After layout, so the terminal already has the size the content was captured at
            // (the resize's %layout-change is answered before the captures).
            Dispatcher.UIThread.Post(() =>
            {
                _restoring.Remove(paneId);
                connection.Release(content);
            }, DispatcherPriority.Background);
        }
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
            Log.Warn("tmux", $"bad layout for @{id}: {ex.Message} ({layoutString})");
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
            Log.Warn("tmux", $"bad layout for @{window.Id}: {ex.Message} ({layoutString})");
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
        if (window.Id == _currentWindowId) UpdateSessionLabel();
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

    /// <summary>Shows another tmux session in this client (<c>%session-changed</c> follows).</summary>
    public void SwitchSession(string name) => Post("switch-client -t " + TmuxControlProtocol.Quote("=" + name));

    /// <summary>Renames the session shown.</summary>
    public void RenameSession(string name) => Post("rename-session " + TmuxControlProtocol.Quote(name));

    /// <summary>Kills the session shown. tmux then ends this client (<c>%exit</c>), or moves it to another session.</summary>
    public void KillSession() => Post("kill-session -t " + TmuxControlProtocol.Quote("=" + SessionName));

    /// <summary>A new tmux window after the one on show, in its active pane's folder.</summary>
    public void NewWindow() =>
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

    /// <summary>
    /// Ctrl+Shift+PgUp/PgDn: the previous/next tmux window, wrapping like tmux. Only with
    /// more than one window, so a single window leaves the keys to the program.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift)
            || e.Key is not (Key.PageUp or Key.PageDown) || _windows.Count < 2) return;
        e.Handled = true;
        var ordered = _windows.Values.OrderBy(w => w.Index).ToList();
        int at = ordered.FindIndex(w => w.Id == _currentWindowId);
        int step = e.Key == Key.PageDown ? 1 : -1;
        var next = ordered[((at < 0 ? 0 : at + step) % ordered.Count + ordered.Count) % ordered.Count];
        ShowWindow(next.Id);
        Post($"select-window -t @{next.Id}");
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
        UpdateSessionLabel();
        _root.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(PaneLayoutView.PanesChangedEvent));
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
        // tmux sizes the whole client in cells, so every pane in the tab shares one font size.
        if (_terminals.Values.FirstOrDefault() is { } sibling) tc.FontSize = sibling.FontSize;
        tc.PropertyChanged += (_, e) =>
        {
            if (e.Property == TemplatedControl.FontSizeProperty) OnPaneFontSizeChanged(tc);
        };
        // Pastes go through a tmux buffer, so tmux brackets them when the program asked.
        TerminalContextMenuBehavior.SetPasteHandler(tc, text =>
        {
            if (_disposed || _channel.IsClosed) return false;
            ReclaimSize();
            LeaveMode(paneId);
            foreach (var command in TmuxInput.Paste(paneId, text)) _channel.Post(command);
            return true;
        });

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
            var cell = CellSize();
            Debug.WriteLineIf(Math.Abs(cell.Width - tc.CharWidth) > 0.01 || Math.Abs(cell.Height - tc.CharHeight) > 0.01,
                $"[tmux] measured cell {cell} differs from the terminal's {tc.CharWidth}x{tc.CharHeight}");
        }
        if (tc.IsLoaded) Attach();
        else
        {
            void OnLoaded(object? s, Avalonia.Interactivity.RoutedEventArgs e) { tc.Loaded -= OnLoaded; Attach(); }
            tc.Loaded += OnLoaded;
        }

        _terminals[paneId] = tc;
        ScheduleClientSize();
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
        _paused.TryRemove(entry.Key, out _);
        _inMode.TryRemove(entry.Key, out _);
        if (_connections.TryRemove(entry.Key, out var connection)) connection.Close();
        TerminalRemoved?.Invoke(tc);
    }

    // -----------------------------------------------------------------------
    // Size
    // -----------------------------------------------------------------------

    /// <summary>
    /// The pixel size of one cell, measured the way the terminal measures it (a "W" in the
    /// pane font), so it is right before any terminal has loaded and right after a zoom.
    /// </summary>
    private Size CellSize()
    {
        if (_terminals.Values.FirstOrDefault() is not { } tc) return FallbackCell;
        var font = (tc.FontFamily, tc.FontSize, tc.FontStyle, tc.FontWeight);
        if (_cellFont != font)
        {
            var text = new FormattedText("W", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(tc.FontFamily, tc.FontStyle, tc.FontWeight, FontStretch.Normal), tc.FontSize, Brushes.Black);
            _cell = new Size(text.Width, text.Height);
            _cellFont = font;
        }
        return _cell.Width > 0 && _cell.Height > 0 ? _cell : FallbackCell;
    }

    /// <summary>Ctrl+wheel zoomed one pane: give every pane in the tab that size, then re-size tmux.</summary>
    private void OnPaneFontSizeChanged(TerminalControl changed)
    {
        if (_syncingFont || _disposed) return;
        _syncingFont = true;
        try
        {
            foreach (var tc in _terminals.Values)
                if (!ReferenceEquals(tc, changed)) tc.FontSize = changed.FontSize;
        }
        finally { _syncingFont = false; }
        foreach (var w in _windows.Values) w.View.InvalidateMeasure();
        ScheduleClientSize();
    }

    private void ScheduleClientSize()
    {
        if (_disposed) return;
        _sizeTimer.Stop();
        _sizeTimer.Start();
    }

    /// <summary>Tells tmux how many cells the tab has room for (see <see cref="PaneLayoutView.ExternalCellsFor"/>).</summary>
    private void SendClientSize()
    {
        if (_disposed || _channel.IsClosed || _terminals.Count == 0
            || _host.Bounds.Width <= 0 || _host.Bounds.Height <= 0) return;
        var (cols, rows) = PaneLayoutView.ExternalCellsFor(_host.Bounds.Size, CellSize());
        cols = Math.Max(10, cols);
        rows = Math.Max(4, rows);
        if ((cols, rows) == _sentSize) return;
        _sentSize = (cols, rows);
        _channel.Post(string.Create(CultureInfo.InvariantCulture, $"refresh-client -C {cols}x{rows}"));
        _sizeHintTimer.Stop();
        _sizeHintTimer.Start();
    }

    /// <summary>
    /// Shows the session name, plus a hint when the window on show isn't the size of this
    /// tab: tmux has sized it for another attached client (<c>window-size</c>).
    /// </summary>
    private void UpdateSessionLabel()
    {
        _sessionLabel.Text = "tmux: " + SessionName;
        _sessionLabel.Foreground = Brushes.Gray;
        ToolTip.SetTip(_sessionLabel, null);
        _contestedWindowId = -1;

        // While our own resize is in flight, a mismatch is just tmux not having answered yet.
        if (_sizeHintTimer.IsEnabled || ActivePanes is not { } view || _currentWindowId is not { } id || _sentSize == default) return;
        var (cols, rows) = (view.Layout.Width, view.Layout.Height);
        if ((cols, rows) == _sentSize) return;
        _contestedWindowId = id;
        _sessionLabel.Text += string.Create(CultureInfo.InvariantCulture, $"  ·  {cols}×{rows}, sized by another client");
        _sessionLabel.Foreground = SizeHintBrush;
        ToolTip.SetTip(_sessionLabel,
            string.Create(CultureInfo.InvariantCulture, $"This tab has room for {_sentSize.Cols}×{_sentSize.Rows}, but tmux has sized the window for another attached client.\n") +
            "With tmux's default window-size (latest), typing here gives the window back to this tab. " +
            "With window-size smallest or largest, the window keeps the other client's size.");
    }

    /// <summary>
    /// Terminal write thread: the user typed while another client has the window's size.
    /// Selecting the window makes this the latest client, which is what typing in a normal
    /// tmux client does (keys sent with <c>send-keys</c> don't count), so with
    /// <c>window-size latest</c> tmux sizes the window for this tab again.
    /// </summary>
    private void ReclaimSize()
    {
        int id = _contestedWindowId;
        if (id < 0) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastReclaim) < (long)ReclaimInterval.TotalMilliseconds) return;
        Interlocked.Exchange(ref _lastReclaim, now);
        _channel.Post($"select-window -t @{id}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sizeTimer.Stop();
        _sizeHintTimer.Stop();
        try { _channel.Dispose(); } catch { }
        foreach (var c in _connections.Values) c.Close();
        _connections.Clear();
    }
}
