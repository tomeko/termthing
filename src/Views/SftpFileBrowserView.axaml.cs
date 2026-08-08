using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Collections;
using Renci.SshNet;
using TermThing.Sessions;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TermThing.Configuration;
using TermThing.Editor;
using TermThing.Sftp;

namespace TermThing.Views;

public sealed class SftpEntry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public bool IsDirectory { get; init; }
    public bool IsParentLink { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string Permissions { get; init; } = string.Empty;
    public int OwnerId { get; init; }
    public int GroupId { get; init; }

    public bool IsHidden => !IsParentLink && Name.StartsWith('.');

    // Yellow tint for folders, inherited foreground (white in dark theme) for everything else.
    public IBrush IconForeground => IsDirectory && !IsParentLink
        ? new SolidColorBrush(Color.FromRgb(255, 198, 64))
        : Brushes.White;

    // Dim hidden entries (dotfiles/dotdirs) to 50% opacity.
    public double IconOpacity => IsHidden ? 0.5 : 1.0;

    private bool _isDropTarget;
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set { if (_isDropTarget != value) { _isDropTarget = value; OnPropertyChanged(); } }
    }

    public string TypeIcon
    {
        get
        {
            if (IsParentLink) return "↩";
            if (IsDirectory) return "📁";
            return System.IO.Path.GetExtension(Name).ToLowerInvariant() switch
            {
                ".sh" or ".bash" or ".zsh" or ".fish" or ".py" or ".rb"
                    or ".pl" or ".ps1" or ".cmd" or ".bat" => "📜",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp"
                    or ".svg" or ".webp" or ".ico" or ".tiff" or ".tif" => "🖼",
                ".mp3" or ".wav" or ".ogg" or ".flac" or ".aac"
                    or ".m4a" or ".opus" or ".wma" => "🎵",
                ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv"
                    or ".flv" or ".webm" or ".m4v" => "🎬",
                ".zip" or ".tar" or ".gz" or ".bz2" or ".xz"
                    or ".7z" or ".rar" or ".tgz" or ".tbz2" or ".zst" => "📦",
                _ => "📄",
            };
        }
    }

    public string SizeDisplay => IsDirectory ? string.Empty : Size switch
    {
        >= 1_073_741_824 => $"{Size / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{Size / 1_048_576.0:F1} MB",
        >= 1_024 => $"{Size / 1_024.0:F1} KB",
        _ => $"{Size} B",
    };

    public string ModifiedDisplay => Modified.HasValue
        ? Modified.Value.ToString("yyyy-MM-dd HH:mm")
        : string.Empty;

    public string OwnerDisplay => IsParentLink ? string.Empty : OwnerId.ToString();
    public string GroupDisplay => IsParentLink ? string.Empty : GroupId.ToString();
}

public partial class SftpFileBrowserView : UserControl
{
    private readonly SftpClient _sftpClient;
    private readonly TransferQueue _transferQueue;
    private readonly EditorRegistry? _editorRegistry;
    private SshClient? _sshClient;
    private string _currentPath = "/";
    private bool _navigating;

    // Per-session delete confirmation suppression
    private bool _suppressDeleteConfirmThisSession;

    /// <summary>
    /// Stable identifier for this SFTP session, used as the key in
    /// <see cref="EditorRegistry"/> so that the same file opened twice is
    /// de-duplicated rather than launching a second editor window.
    /// </summary>
    public Guid SessionEditorId { get; } = Guid.NewGuid();

    private TextBox _pathBox = null!;
    private Button _refreshButton = null!;
    private Button _syncToTerminalButton = null!;
    private DataGrid _filesGrid = null!;
    private CheckBox _followLocationCheckBox = null!;
    private CheckBox _sysmonCheckBox = null!;
    private CheckBox _dockerMonCheckBox = null!;
    private TextBlock _statusText = null!;
    private MenuItem _menuOpen = null!;
    private MenuItem _menuOpenWith = null!;
    private MenuItem _menuTail = null!;
    private MenuItem _menuDownloadTo = null!;
    private MenuItem _menuRename = null!;
    private MenuItem _menuDelete = null!;
    private MenuItem _menuProperties = null!;
    private MenuItem _menuBookmarkFolder = null!;
    private MenuItem _menuNewDirectory = null!;
    private MenuItem _menuNewFile = null!;
    private Separator _blankAreaSeparator = null!;
    private TransferProgressOverlay _transferOverlay = null!;
    private Border _dropOverlay = null!;

    private bool _contextMenuOnBlankArea;

    // Bookmark panel controls
    private Grid _mainContentGrid = null!;
    private GridSplitter _bookmarkSplitter = null!;
    private ScrollViewer _bookmarksBody = null!;
    private StackPanel _bookmarksListPanel = null!;
    private TextBlock _bookmarksArrow = null!;
    private TextBlock _bookmarksHeaderText = null!;

    // Upload drag state
    private SftpEntry? _dropTargetDir;  // folder row being hovered (null = whole view)

    // Drag-to-download state
    private SftpEntry? _dragOutPending;  // entry the user started dragging
    private Point _dragOutStartPos;
    private PointerPressedEventArgs? _dragOutPointerArgs;
    private bool _dragOutInProgress;

    // Internal drag tracking — while a drag originated from THIS view is in flight,
    // _internalDragSource holds the source entry. OnDrop checks this to distinguish
    // an internal move-within-SFTP from an OS-level upload (file drag from desktop).
    private SftpEntry? _internalDragSource;

    // Set in the pointer-pressed tunnel handler when the user left-clicks the
    // row that was already the sole selected entry (no Shift/Ctrl). On pointer
    // release — if no drag started — we clear the selection so users can
    // toggle off a single highlight.
    private bool _clickedSoleSelectedItem;

    private string? _lastTerminalDir;

    // Per-session definition and persistence callback for bookmark mutations.
    private SessionDefinition? _definition;
    private Action? _saveConfig;

    // Shell command injection callback (set by SshSessionInstance after shell stream is ready).
    private Action<string>? _sendShellCommand;

    // Remote-monitoring callbacks wired by SshSessionInstance after construction.
    // Returning false (or completing with false) tells the SFTP view to revert
    // the corresponding checkbox without firing again.
    internal Func<bool, bool>? SysmonToggleRequested { get; set; }
    internal Func<bool, Task<bool>>? DockerMonToggleRequested { get; set; }
    internal Action<string>? TailFileRequested { get; set; }

    private bool _suppressMonitorEvents;

    // Bookmark drag state
    private int? _bookmarkDragSourceIndex;
    private Point _bookmarkDragStartPos;
    private bool _bookmarkDragActive;

    // Remembered height for restore when expanding (persisted in TempSettings).
    private double _bookmarksBodyHeight = 160;
    private bool _bookmarksExpanded;

    public ObservableCollection<SftpEntry> Entries { get; } = new();
    private DataGridCollectionView? _entriesView;
    private DataGridColumn? _sortColumn;
    private ListSortDirection _sortDir;
    public string CurrentPath => _currentPath;
    public bool FollowLocation => _followLocationCheckBox?.IsChecked == true;

    public SftpFileBrowserView(
        SftpClient sftpClient,
        SshClient? sshClient = null,
        EditorRegistry? editorRegistry = null,
        SessionDefinition? definition = null,
        Action? saveConfig = null)
    {
        _sftpClient      = sftpClient ?? throw new ArgumentNullException(nameof(sftpClient));
        _sshClient       = sshClient;
        _editorRegistry  = editorRegistry;
        _definition      = definition;
        _saveConfig      = saveConfig;
        InitializeComponent();

        _pathBox               = this.FindControl<TextBox>("PathBox")!;
        _refreshButton         = this.FindControl<Button>("RefreshButton")!;
        _syncToTerminalButton  = this.FindControl<Button>("SyncToTerminalButton")!;
        _filesGrid             = this.FindControl<DataGrid>("FilesGrid")!;
        _followLocationCheckBox = this.FindControl<CheckBox>("FollowLocationCheckBox")!;
        _sysmonCheckBox        = this.FindControl<CheckBox>("SysmonCheckBox")!;
        _dockerMonCheckBox     = this.FindControl<CheckBox>("DockerMonCheckBox")!;
        _statusText            = this.FindControl<TextBlock>("StatusText")!;
        _menuOpen         = this.FindControl<MenuItem>("MenuOpen")!;
        _menuOpenWith     = this.FindControl<MenuItem>("MenuOpenWith")!;
        _menuTail         = this.FindControl<MenuItem>("MenuTail")!;
        _menuDownloadTo   = this.FindControl<MenuItem>("MenuDownloadTo")!;
        _menuRename       = this.FindControl<MenuItem>("MenuRename")!;
        _menuDelete       = this.FindControl<MenuItem>("MenuDelete")!;
        _menuProperties   = this.FindControl<MenuItem>("MenuProperties")!;
        _menuBookmarkFolder  = this.FindControl<MenuItem>("MenuBookmarkFolder")!;
        _menuNewDirectory = this.FindControl<MenuItem>("MenuNewDirectory")!;
        _menuNewFile      = this.FindControl<MenuItem>("MenuNewFile")!;
        _blankAreaSeparator = this.FindControl<Separator>("BlankAreaSeparator")!;
        _transferOverlay       = this.FindControl<TransferProgressOverlay>("TransferOverlay")!;
        _dropOverlay           = this.FindControl<Border>("DropOverlay")!;

        _mainContentGrid     = this.FindControl<Grid>("MainContentGrid")!;
        _bookmarkSplitter    = this.FindControl<GridSplitter>("BookmarkSplitter")!;
        _bookmarksBody       = this.FindControl<ScrollViewer>("BookmarksBody")!;
        _bookmarksListPanel  = this.FindControl<StackPanel>("BookmarksListPanel")!;
        _bookmarksArrow      = this.FindControl<TextBlock>("BookmarksArrow")!;
        _bookmarksHeaderText = this.FindControl<TextBlock>("BookmarksHeaderText")!;

        _entriesView = new DataGridCollectionView(Entries);
        _filesGrid.ItemsSource = _entriesView;
        _filesGrid.Sorting += OnGridSorting;

        // Wire transfer queue to the progress overlay
        _transferQueue = new TransferQueue(_sftpClient);
        _transferOverlay.Bind(_transferQueue);
        _transferQueue.TransferError += (_, msg) =>
            Dispatcher.UIThread.Post(() => SetStatus($"Transfer error: {msg}"));
        _transferQueue.QueueEmpty += (_, _) =>
            Dispatcher.UIThread.Post(Refresh);

        // Set up drop (upload from OS → SFTP)
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent,  OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent,      OnDrop);

        // Set up drag-out (download SFTP → OS)
        _filesGrid.AddHandler(PointerPressedEvent,  OnFilesGridPointerPressed,  RoutingStrategies.Tunnel);
        _filesGrid.AddHandler(PointerMovedEvent,    OnFilesGridPointerMoved,    RoutingStrategies.Tunnel);
        _filesGrid.AddHandler(PointerReleasedEvent, OnFilesGridPointerReleased, RoutingStrategies.Tunnel);

        // F5 refresh at the panel level (tunnel) so it works regardless of which
        // sub-control has focus — the grid-scoped OnFilesGridKeyDown only fires when
        // a row is focused, which rarely holds (the app focuses the terminal).
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);

        // Restore persisted column widths
        RestoreColumnWidths();

        // Persist column widths when they change (debounced via a timer per column)
        SubscribeColumnWidthPersistence();

        // Bookmark panel: restore persisted state and wire drag handlers
        var savedHeight = SettingsService.Temp.BookmarksHeightPx;
        if (savedHeight > 20) _bookmarksBodyHeight = savedHeight;
        _bookmarksListPanel.AddHandler(PointerPressedEvent,  OnBookmarksPanelPointerPressed,  RoutingStrategies.Tunnel);
        _bookmarksListPanel.AddHandler(PointerMovedEvent,    OnBookmarksPanelPointerMoved,    RoutingStrategies.Tunnel);
        _bookmarksListPanel.AddHandler(PointerReleasedEvent, OnBookmarksPanelPointerReleased, RoutingStrategies.Tunnel);
        SetBookmarksExpanded(SettingsService.Temp.BookmarksExpanded, save: false);
        UpdateBookmarksHeader();
        RebuildBookmarksList();
    }

    /// <summary>
    /// Called by <see cref="SshSessionInstance"/> once the SSH shell stream is
    /// ready. Enables bookmark activation to inject <c>cd</c> commands into
    /// the remote shell.
    /// </summary>
    internal void SetShellCommand(Action<string> sendCommand)
    {
        _sendShellCommand = sendCommand;
        Dispatcher.UIThread.Post(UpdateSyncButtonVisibility);
    }

    public void NavigateTo(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = "/" + path.Trim('/');
        if (path.Length == 0) path = "/";
        _ = NavigateAsync(path);
    }

    private async Task NavigateAsync(string path)
    {
        if (_navigating) return;
        if (!_sftpClient.IsConnected) return;
        _navigating = true;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SetStatus(null);
                _refreshButton.IsEnabled = false;
            });

            var files = await Task.Run(() => FetchDirectory(ref path));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Entries.Clear();
                if (path != "/")
                    Entries.Add(new SftpEntry
                    {
                        IsDirectory  = true,
                        IsParentLink = true,
                        Name         = "..",
                        FullPath     = GetParentPath(path),
                    });
                foreach (var entry in files)
                    Entries.Add(entry);
                _currentPath     = path;
                _pathBox.Text    = _currentPath;
                UpdateSyncButtonVisibility();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetStatus($"Error: {ex.Message}"));
        }
        finally
        {
            _navigating = false;
            await Dispatcher.UIThread.InvokeAsync(() => _refreshButton.IsEnabled = true);
        }
    }

    /// <summary>
    /// Lists <paramref name="path"/>. If the server rejects root "/" with
    /// <see cref="Renci.SshNet.Common.SftpPathNotFoundException"/>, falls back to
    /// <see cref="SftpClient.WorkingDirectory"/> (usually the home directory) and
    /// updates <paramref name="path"/> accordingly.
    /// </summary>
    private List<SftpEntry> FetchDirectory(ref string path)
    {
        try
        {
            return ListPath(path);
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) when (path == "/")
        {
            // Server doesn't allow listing root — fall back to home directory
            path = _sftpClient.WorkingDirectory;
            return ListPath(path);
        }
    }

    private List<SftpEntry> ListPath(string path) =>
        _sftpClient.ListDirectory(path)
            .Where(f => f.Name != "." && f.Name != "..")
            .OrderByDescending(f => f.IsDirectory)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new SftpEntry
            {
                IsDirectory = f.IsDirectory,
                Name        = f.Name,
                Size        = f.Attributes.Size,
                Modified    = f.LastWriteTime,
                FullPath    = f.FullName,
                Permissions = BuildPermissionString(f),
                OwnerId     = f.Attributes.UserId,
                GroupId     = f.Attributes.GroupId,
            })
            .ToList();

    public void Refresh() => NavigateTo(_currentPath);

    public void OnTerminalDirectoryChanged(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            try { path = new Uri(path).LocalPath; }
            catch
            {
                var idx = path.IndexOf('/', 7);
                path = idx >= 0 ? path[idx..] : path;
            }
        }

        _lastTerminalDir = path;
        UpdateSyncButtonVisibility();

        if (!FollowLocation || string.IsNullOrWhiteSpace(path)) return;
        NavigateTo(path);
    }

    private void UpdateSyncButtonVisibility()
    {
        if (_syncToTerminalButton == null) return;
        _syncToTerminalButton.IsVisible =
            _sendShellCommand != null &&
            !string.IsNullOrEmpty(_lastTerminalDir) &&
            !string.Equals(_currentPath, _lastTerminalDir, StringComparison.Ordinal);
    }

    private void OnSyncToTerminalClicked(object? sender, RoutedEventArgs e)
    {
        if (_sendShellCommand == null) return;
        var escaped = _currentPath.Replace("'", "'\\''" );
        _sendShellCommand($"cd '{escaped}'\n");
    }

    private void OnFollowLocationChecked(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastTerminalDir))
            NavigateTo(_lastTerminalDir);
    }

    private void OnSysmonCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressMonitorEvents) return;
        if (SysmonToggleRequested == null) return;
        var on = _sysmonCheckBox.IsChecked == true;
        var ok = SysmonToggleRequested(on);
        if (!ok && on)
            SetMonitorChecked(_sysmonCheckBox, false);
    }

    private async void OnDockerMonCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressMonitorEvents) return;
        if (DockerMonToggleRequested == null) return;
        var on = _dockerMonCheckBox.IsChecked == true;
        // Disable interaction during the docker probe so the user can't double-click.
        _dockerMonCheckBox.IsEnabled = false;
        try
        {
            var ok = await DockerMonToggleRequested(on);
            if (!ok && on)
                SetMonitorChecked(_dockerMonCheckBox, false);
        }
        finally
        {
            _dockerMonCheckBox.IsEnabled = true;
        }
    }

    private async void OnMenuTailClicked(object? sender, RoutedEventArgs e)
    {
        if (_filesGrid.SelectedItem is not SftpEntry entry || entry.IsDirectory || entry.IsParentLink)
            return;
        TailFileRequested?.Invoke(entry.FullPath);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sets <see cref="SysmonCheckBox"/> / <see cref="DockerMonCheckBox"/> initial
    /// state from persisted settings without invoking the toggle callback.
    /// </summary>
    internal void SetInitialMonitorState(bool sysmonEnabled, bool dockerMonEnabled)
    {
        _suppressMonitorEvents = true;
        try
        {
            _sysmonCheckBox.IsChecked    = sysmonEnabled;
            _dockerMonCheckBox.IsChecked = dockerMonEnabled;
        }
        finally
        {
            _suppressMonitorEvents = false;
        }
    }

    private void SetMonitorChecked(CheckBox cb, bool value)
    {
        _suppressMonitorEvents = true;
        try { cb.IsChecked = value; }
        finally { _suppressMonitorEvents = false; }
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => Refresh();

    /// <summary>
    /// Raised when the user clicks the SFTP browser's close (✕) button. The owning
    /// <c>SshSessionInstance</c> handles it by tearing down the SFTP channel.
    /// </summary>
    public event EventHandler? CloseRequested;

    private void OnCloseSftpClicked(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnPathBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var text = _pathBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text)) NavigateTo(text);
            e.Handled = true;
        }
    }

    private void OnFilesGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only act on a double-tap that actually landed on a data row — rejects
        // column headers, the scrollbar and empty space in one check.
        if (e.Source is not Visual src ||
            !src.GetSelfAndVisualAncestors().OfType<DataGridRow>().Any())
            return;

        if (_filesGrid.SelectedItem is not SftpEntry entry) return;

        if (entry.IsDirectory)
        {
            NavigateTo(entry.FullPath);
        }
        else
        {
            // Open file using the registered app or prompt the user
            _ = OpenEntryAsync(entry);
        }
    }

    // -----------------------------------------------------------------------
    // Column sorting — folders stay grouped above files and ".." stays pinned,
    // the clicked column orders within each group.
    // -----------------------------------------------------------------------

    // Avalonia's DataGrid only renders the header sort arrow when the column's
    // CustomSortComparer instance is reference-equal to a
    // DataGridComparerSortDescription.SourceComparer in the view's
    // SortDescriptions (DataGridColumn.GetSortDescription). It has no public
    // SortDirection setter. So: handle Sorting ourselves, point the column's
    // CustomSortComparer at our comparer, and feed the view a custom
    // DataGridComparerSortDescription subclass whose Comparer keeps folders
    // grouped / ".." pinned WITHOUT the base class's blanket direction-negation
    // (which would otherwise unpin/ungroup on descending) while still reporting
    // Direction so the arrow points the right way.
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true; // we drive the sort via the collection view ourselves
        if (_entriesView == null) return;

        _sortDir = _sortColumn == e.Column && _sortDir == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortColumn = e.Column;

        var comparer = new SftpEntryComparer(e.Column.Header as string, _sortDir);
        e.Column.CustomSortComparer = comparer; // matched by reference for the arrow

        _entriesView.SortDescriptions.Clear();
        _entriesView.SortDescriptions.Add(new SftpSortDescription(comparer, _sortDir));
    }

    /// <summary>
    /// A comparer-based sort description whose <see cref="Comparer"/> is our
    /// direction-aware comparer verbatim — overriding the base behaviour that
    /// negates the whole result for descending (which would drag files above
    /// folders and unpin ".."). <see cref="Direction"/> is still reported so
    /// the header arrow renders correctly.
    /// </summary>
    private sealed class SftpSortDescription : DataGridComparerSortDescription
    {
        private readonly SftpEntryComparer _comparer;

        public SftpSortDescription(SftpEntryComparer comparer, ListSortDirection direction)
            : base(comparer, direction) => _comparer = comparer;

        public override IComparer<object> Comparer => _comparer;

        public override DataGridSortDescription SwitchSortDirection() =>
            new SftpSortDescription(
                new SftpEntryComparer(_comparer.Key,
                    Direction == ListSortDirection.Ascending
                        ? ListSortDirection.Descending
                        : ListSortDirection.Ascending),
                Direction == ListSortDirection.Ascending
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);
    }

    private sealed class SftpEntryComparer : IComparer, IComparer<object>
    {
        public string? Key { get; }
        private readonly int _dir;

        public SftpEntryComparer(string? key, ListSortDirection d)
        {
            Key  = key;
            _dir = d == ListSortDirection.Descending ? -1 : 1;
        }

        public int Compare(object? a, object? b)
        {
            var x = (SftpEntry)a!;
            var y = (SftpEntry)b!;
            if (x.IsParentLink != y.IsParentLink) return x.IsParentLink ? -1 : 1; // ".." pinned
            if (x.IsDirectory  != y.IsDirectory)  return x.IsDirectory  ? -1 : 1; // dirs first

            int c = Key switch
            {
                "Size"        => x.Size.CompareTo(y.Size),
                "Modified"    => Nullable.Compare(x.Modified, y.Modified),
                "Permissions" => string.Compare(x.Permissions, y.Permissions, StringComparison.Ordinal),
                "Owner"       => x.OwnerId.CompareTo(y.OwnerId),
                "Group"       => x.GroupId.CompareTo(y.GroupId),
                _             => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase),
            };
            return c * _dir;
        }
    }

    // -----------------------------------------------------------------------
    // Context menu
    // -----------------------------------------------------------------------

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Selection-aware enable/disable. The DataGrid keeps multi-selection alive
        // on right-click (only switches selection if you right-click a non-selected
        // row), so SelectedItems is the authoritative list to reason about.
        var selection = _filesGrid.SelectedItems?
            .OfType<SftpEntry>()
            .Where(x => !x.IsParentLink)
            .ToList() ?? [];

        int count    = selection.Count;
        int dirCount = selection.Count(x => x.IsDirectory);
        bool single  = count == 1;
        bool anyDir  = dirCount > 0;
        bool allFiles = count > 0 && dirCount == 0;
        bool oneDir   = single && anyDir;

        // File-only, single-selection operations
        _menuOpen.IsEnabled     = single && allFiles;
        _menuOpenWith.IsEnabled = single && allFiles;
        _menuTail.IsEnabled     = single && allFiles
                                  && _sshClient?.IsConnected == true
                                  && TailFileRequested != null;

        // Single-selection (file or directory)
        _menuRename.IsEnabled = single;

        // Multi-selection capable
        _menuDownloadTo.IsEnabled = count >= 1;
        _menuDelete.IsEnabled     = count >= 1;
        _menuDelete.Header        = count > 1 ? $"Delete {count} items" : "Delete";

        // Single-directory-only operations — hide rather than disable; they're
        // contextually meaningless for files or multi-selection.
        _menuProperties.IsVisible    = oneDir && _sshClient?.IsConnected == true;
        _menuBookmarkFolder.IsVisible = oneDir && _definition != null;

        // "New Directory" / "New File" act on the current folder, not any
        // selection — disable when multiple rows are highlighted so the menu
        // doesn't suggest the action is selection-relative.
        bool canCreate = count <= 1;
        _menuNewDirectory.IsEnabled = canCreate;
        _menuNewFile.IsEnabled      = canCreate;

        // Rebuild the "Download to ▶" submenu dynamically
        RebuildDownloadToSubMenu();
    }

    private async void OnMenuPropertiesClicked(object? sender, RoutedEventArgs e)
    {
        if (_filesGrid.SelectedItem is not SftpEntry entry || !entry.IsDirectory || entry.IsParentLink)
            return;
        if (_sshClient == null || !_sshClient.IsConnected)
            return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new FolderPropertiesDialog(_sshClient, entry.FullPath);
        await dialog.ShowDialog(owner!);
    }

    private void OnMenuBookmarkFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (_filesGrid.SelectedItem is not SftpEntry entry || !entry.IsDirectory || entry.IsParentLink) return;

        var bookmarks = GetBookmarks();
        if (bookmarks.Any(b => b.AbsolutePath == entry.FullPath)) return;

        bookmarks.Add(new Bookmark { Name = entry.FullPath, AbsolutePath = entry.FullPath });
        SaveBookmarks(bookmarks);

        if (!_bookmarksExpanded)
            SetBookmarksExpanded(true, save: true);
    }

    private void RebuildDownloadToSubMenu()
    {
        _menuDownloadTo.Items.Clear();

        // 1. Desktop
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
        {
            var item = new MenuItem { Header = $"🖥  Desktop  ({desktop})" };
            item.Click += (_, _) => DownloadTo(desktop);
            _menuDownloadTo.Items.Add(item);
        }

        // 2. Recent folders (most-recent first, skip if same as Desktop)
        var recents = SettingsService.Temp.RecentDownloadFolders
            .Where(p => !string.Equals(p, desktop, StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();

        if (recents.Count > 0)
            _menuDownloadTo.Items.Add(new Separator());

        foreach (var folder in recents)
        {
            var folderCopy = folder;
            var item = new MenuItem { Header = folderCopy };
            item.Click += (_, _) => DownloadTo(folderCopy);
            _menuDownloadTo.Items.Add(item);
        }

        // 3. Browse…
        _menuDownloadTo.Items.Add(new Separator());
        var browse = new MenuItem { Header = "Browse…" };
        browse.Click += async (_, _) =>
        {
            var host = TopLevel.GetTopLevel(this);
            if (host == null) return;
            var folders = await host.StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Download to…", AllowMultiple = false });
            if (folders.Count > 0)
                DownloadTo(folders[0].Path.LocalPath);
        };
        _menuDownloadTo.Items.Add(browse);
    }

    private void DownloadTo(string destDir)
    {
        if (_filesGrid.SelectedItem is not SftpEntry entry || entry.IsParentLink) return;

        // Push to recent list (most-recent first, capped at 5, no duplicates)
        var recents = SettingsService.Temp.RecentDownloadFolders;
        recents.RemoveAll(p => string.Equals(p, destDir, StringComparison.OrdinalIgnoreCase));
        recents.Insert(0, destDir);
        if (recents.Count > 5) recents.RemoveRange(5, recents.Count - 5);
        SettingsService.SaveTemp();

        _ = EnqueueDownloadAsync(entry, destDir);
    }

    private async Task EnqueueDownloadAsync(SftpEntry entry, string destDir)
    {
        SetStatus("Building download list…");
        List<TransferJob> jobs;
        try
        {
            jobs = await Task.Run(() =>
            {
                var list = new List<TransferJob>();
                BuildDownloadJobsRecursive(entry, destDir, list);
                return list;
            });
        }
        catch (Exception ex)
        {
            SetStatus($"Download failed: {ex.Message}");
            return;
        }

        SetStatus(null);
        if (jobs.Count > 0)
            _transferQueue.Enqueue(jobs);
    }

    private void BuildDownloadJobsRecursive(SftpEntry entry, string localDir, List<TransferJob> jobs)
    {
        var localPath = Path.Combine(localDir, entry.Name);
        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(localPath);
            foreach (var child in _sftpClient.ListDirectory(entry.FullPath))
            {
                if (child.Name == "." || child.Name == "..") continue;
                var childEntry = new SftpEntry
                {
                    IsDirectory = child.IsDirectory,
                    Name        = child.Name,
                    FullPath    = child.FullName,
                    Size        = child.Length,
                };
                BuildDownloadJobsRecursive(childEntry, localPath, jobs);
            }
        }
        else
        {
            jobs.Add(new TransferJob
            {
                IsUpload   = false,
                LocalPath  = localPath,
                RemotePath = entry.FullPath,
                TotalBytes = entry.Size,
            });
        }
    }

    private async void OnMenuOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (_filesGrid.SelectedItem is SftpEntry { IsDirectory: false } entry)
            await OpenEntryAsync(entry);
    }

    private async void OnMenuNewDirectoryClicked(object? sender, RoutedEventArgs e)
    {
        var host = (TopLevel.GetTopLevel(this) as Window)!;
        var dialog = new RenameDialog("") { Title = "New Directory" };
        var name = await dialog.ShowDialog<string?>(host);
        if (string.IsNullOrWhiteSpace(name)) return;
        var remotePath = _currentPath.TrimEnd('/') + "/" + name;
        try
        {
            await Task.Run(() => _sftpClient.CreateDirectory(remotePath));
            Refresh();
        }
        catch (Exception ex)
        {
            SetStatus($"Create directory failed: {ex.Message}");
        }
    }

    private async void OnMenuNewFileClicked(object? sender, RoutedEventArgs e)
    {
        var host = (TopLevel.GetTopLevel(this) as Window)!;
        var dialog = new RenameDialog("") { Title = "New File" };
        var name = await dialog.ShowDialog<string?>(host);
        if (string.IsNullOrWhiteSpace(name)) return;
        var remotePath = _currentPath.TrimEnd('/') + "/" + name;
        try
        {
            await Task.Run(() =>
            {
                using var stream = new System.IO.MemoryStream();
                _sftpClient.UploadFile(stream, remotePath);
            });
            Refresh();
        }
        catch (Exception ex)
        {
            SetStatus($"Create file failed: {ex.Message}");
        }
    }

    private async Task OpenEntryWithAsync(SftpEntry entry, ApplicationEntry app)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;
        var displayHost = _sshClient is not null
            ? $"{_sshClient.ConnectionInfo.Username}@{_sshClient.ConnectionInfo.Host}"
            : _sftpClient.ConnectionInfo.Host;
        var opener = new SftpFileOpener(_sftpClient, SessionEditorId, displayHost, host, _editorRegistry);
        try
        {
            await opener.OpenWithEntryAsync(entry.FullPath, app);
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}");
        }
    }

    private void OpenApplicationsSettings()
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;
        var settings = new SettingsWindow();
        settings.OpenToApplications();
        _ = settings.ShowDialog(host);
    }

    private async void OnMenuDeleteClicked(object? sender, RoutedEventArgs e)
    {
        var entries = _filesGrid.SelectedItems?
            .OfType<SftpEntry>()
            .Where(x => !x.IsParentLink)
            .ToList() ?? [];
        if (entries.Count == 0) return;
        await DeleteEntriesAsync(entries);
    }

    private async void OnMenuRenameClicked(object? sender, RoutedEventArgs e)
    {
        if (_filesGrid.SelectedItem is not SftpEntry entry || entry.IsParentLink) return;
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;

        var dialog = new RenameDialog(entry.Name) { Title = "Rename" };
        var newName = await dialog.ShowDialog<string?>(host);
        if (string.IsNullOrWhiteSpace(newName) || newName == entry.Name) return;
        if (newName.Contains('/'))
        {
            SetStatus("Rename failed: name cannot contain '/'.");
            return;
        }

        var oldPath = entry.FullPath;
        var newPath = _currentPath.TrimEnd('/') + "/" + newName;
        try
        {
            await Task.Run(() => _sftpClient.RenameFile(oldPath, newPath));
            Refresh();
            SetStatus($"Renamed to {newName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Rename failed: {ex.Message}");
        }
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
        }
    }

    private void OnFilesGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            Refresh();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            var entries = _filesGrid.SelectedItems.OfType<SftpEntry>()
                .Where(x => !x.IsParentLink).ToList();
            if (entries.Count > 0)
            {
                _ = DeleteEntriesAsync(entries);
                e.Handled = true;
            }
        }
    }

    private async Task DeleteEntriesAsync(IReadOnlyList<SftpEntry> entries)
    {
        if (entries.Count == 0) return;

        if (!_suppressDeleteConfirmThisSession)
        {
            var host = TopLevel.GetTopLevel(this) as Window;
            var label = entries.Count == 1
                ? entries[0].Name
                : $"{entries.Count} items";
            var isDir = entries.Count == 1 && entries[0].IsDirectory;
            var confirmed = await ShowDeleteConfirmAsync(label, isDir, host);
            if (confirmed == null) return;
            if (confirmed.Value.SuppressFuture) _suppressDeleteConfirmThisSession = true;
            if (!confirmed.Value.Confirmed) return;
        }

        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    if (entry.IsDirectory)
                        DeleteDirectoryRecursive(entry.FullPath);
                    else
                        _sftpClient.DeleteFile(entry.FullPath);
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => SetStatus($"Delete failed: {ex.Message}"));
                }
            }
        });

        Refresh();
    }

    private static async Task<(bool Confirmed, bool SuppressFuture)?> ShowDeleteConfirmAsync(
        string name, bool isDirectory, Window? owner)
    {
        bool confirmed = false;
        bool suppress  = false;

        var chk = new CheckBox
        {
            Content = "Don't ask again this session",
            FontSize = 11,
            Foreground = Brushes.Gray,
        };

        var yesBtn = new Button { Content = "Delete", Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var noBtn  = new Button { Content = "Cancel", IsCancel = true };

        var win = new Window
        {
            Title  = "Confirm delete",
            Width  = 380,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete {(isDirectory ? "directory" : "file")} \"{name}\"?\n" +
                               (isDirectory ? "This will delete all contents recursively." : string.Empty),
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    chk,
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yesBtn, noBtn },
                    },
                },
            },
        };

        yesBtn.Click += (_, _) => { confirmed = true;  suppress = chk.IsChecked == true; win.Close(); };
        noBtn.Click  += (_, _) => { confirmed = false; win.Close(); };
        win.Opened   += (_, _) => yesBtn.Focus();

        if (owner != null)
            await win.ShowDialog(owner);
        else
            win.Show();

        return (confirmed, suppress);
    }

    private void DeleteDirectoryRecursive(string path)
    {
        foreach (var file in _sftpClient.ListDirectory(path))
        {
            if (file.Name == "." || file.Name == "..") continue;
            if (file.IsDirectory)
                DeleteDirectoryRecursive(file.FullName);
            else
                _sftpClient.DeleteFile(file.FullName);
        }
        _sftpClient.DeleteDirectory(path);
    }

    // -----------------------------------------------------------------------
    // Open file
    // -----------------------------------------------------------------------

    private async Task OpenEntryAsync(SftpEntry entry)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;

        // Build a display name for window titles from the SSH client's connection info
        var displayHost = _sshClient is not null
            ? $"{_sshClient.ConnectionInfo.Username}@{_sshClient.ConnectionInfo.Host}"
            : _sftpClient.ConnectionInfo.Host;

        var opener = new SftpFileOpener(
            _sftpClient,
            SessionEditorId,
            displayHost,
            host,
            _editorRegistry);
        try
        {
            await opener.OpenAsync(entry.FullPath);
        }
        catch (Exception ex)
        {
            SetStatus($"Open failed: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Column width persistence
    // -----------------------------------------------------------------------

    private void RestoreColumnWidths()
    {
        var saved = SettingsService.Temp.SftpColumnWidths;
        // Remove any stale pixel-width entries for star-sized columns
        foreach (var col in _filesGrid.Columns)
            if (col.Width.IsStar && col.Header is string h)
                saved.Remove(h);

        // Clamp saved widths against the column's MinWidth so stale tiny values
        // (e.g. from when Name was a star column and got saved as a near-zero pixel)
        // don't make a column invisible.
        foreach (var col in _filesGrid.Columns)
        {
            if (col.Width.IsStar) continue;
            if (col.Header is not string header) continue;
            if (!saved.TryGetValue(header, out var w) || w <= 0) continue;
            var minW = col.MinWidth > 0 ? col.MinWidth : 30;
            col.Width = new DataGridLength(Math.Max(w, minW));
        }
    }

    private void SubscribeColumnWidthPersistence()
    {
        // Debounce: save at most 500 ms after the last resize event
        DispatcherTimer? debounce = null;

        foreach (var col in _filesGrid.Columns)
        {
            // Don't persist the filler star column
            if (col.Width.IsStar) continue;

            col.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name != nameof(col.ActualWidth)) return;
                if (col.Header is not string header) return;

                SettingsService.Temp.SftpColumnWidths[header] = col.ActualWidth;

                debounce?.Stop();
                debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
                    (_, _) => { debounce?.Stop(); SettingsService.SaveTemp(); });
                debounce.Start();
            };
        }
    }

    // -----------------------------------------------------------------------
    // Drop / Upload (OS → SFTP)
    // -----------------------------------------------------------------------

    private void OnFilesGridLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        // Keep the dropTarget CSS class in sync when rows are recycled
        if (e.Row.DataContext is SftpEntry entry)
            e.Row.Classes.Set("dropTarget", entry == _dropTargetDir);
    }

    private void SetDropHighlight(SftpEntry? newTarget)
    {
        // Clear old highlight
        if (_dropTargetDir != null)
        {
            var oldRow = _filesGrid.GetVisualDescendants()
                                   .OfType<DataGridRow>()
                                   .FirstOrDefault(r => r.DataContext == _dropTargetDir);
            oldRow?.Classes.Set("dropTarget", false);
        }

        _dropTargetDir = newTarget;

        // Apply new highlight
        if (newTarget != null)
        {
            var newRow = _filesGrid.GetVisualDescendants()
                                   .OfType<DataGridRow>()
                                   .FirstOrDefault(r => r.DataContext == newTarget);
            newRow?.Classes.Set("dropTarget", true);
        }
    }

    /// <summary>
    /// Describes what the drag source is actually offering. Drag-and-drop from the OS is
    /// the one path whose behaviour is decided entirely by the platform backend, so when
    /// a drop does nothing this is the only way to see whether the payload never arrived
    /// as files or whether it arrived and we rejected it.
    /// </summary>
    private static string DescribeOfferedFormats(DragEventArgs e)
    {
        try
        {
            var names = e.DataTransfer.Formats.Select(f => f.ToString()).ToList();
            return names.Count == 0 ? "(none offered)" : string.Join(", ", names);
        }
        catch (Exception ex)
        {
            return $"(could not enumerate: {ex.GetType().Name})";
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            // Not a file drag as far as this platform's backend is concerned. Surface what
            // it *did* offer: on X11/Wayland a file manager may hand over only URI-list or
            // toolkit-private types, which is invisible from the Windows side.
            SetStatus($"Drag ignored — no file data. Offered: {DescribeOfferedFormats(e)}");
            e.DragEffects = DragDropEffects.None;
            ClearUploadDragState();
            return;
        }

        // Internal drag (the drag originated from this same view) → treat as
        // a move, never show the big upload overlay.
        bool isInternal = _internalDragSource != null;
        e.DragEffects = isInternal ? DragDropEffects.Move : DragDropEffects.Copy;

        var rowEntry = GetEntryUnderNameColumn(e.GetPosition(_filesGrid));
        SetDropHighlight(rowEntry);

        // Show whole-grid overlay only for external uploads, when not hovering a folder.
        _dropOverlay.IsVisible = !isInternal && (rowEntry == null);
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        ClearUploadDragState();
    }

    private void ClearUploadDragState()
    {
        SetDropHighlight(null);
        _dropOverlay.IsVisible = false;
    }

    /// <summary>
    /// Handles an internal SFTP drag-and-drop (a row was dragged within the same view).
    /// Prompts the user for confirmation and renames (moves) the source on the server.
    /// </summary>
    private async Task HandleInternalMoveAsync(SftpEntry src, string destDir)
    {
        if (src.IsParentLink) return;

        var normalizedDest = destDir.TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedDest)) normalizedDest = "/";

        var srcParent = GetParentPath(src.FullPath);
        if (srcParent == normalizedDest)
        {
            SetStatus("Already in this folder.");
            return;
        }

        if (src.IsDirectory)
        {
            var srcPrefix = src.FullPath.TrimEnd('/') + "/";
            if (normalizedDest == src.FullPath.TrimEnd('/') ||
                normalizedDest.StartsWith(srcPrefix, StringComparison.Ordinal))
            {
                SetStatus($"Cannot move '{src.Name}' into itself.");
                return;
            }
        }

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null) return;

        var dialog = new MoveConfirmDialog(itemCount: 1, normalizedDest);
        var confirmed = await dialog.ShowDialog<bool>(host);
        if (!confirmed) return;

        var dstPath = normalizedDest.TrimEnd('/') + "/" + src.Name;
        try
        {
            await Task.Run(() => _sftpClient.RenameFile(src.FullPath, dstPath));
            Refresh();
            SetStatus($"Moved '{src.Name}' to {normalizedDest}");
        }
        catch (Exception ex)
        {
            SetStatus($"Move failed: {ex.Message}");
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var destDir = _dropTargetDir?.FullPath ?? _currentPath;
        ClearUploadDragState();

        // Internal drag dropped back on this view → treat as a move, not an upload.
        if (_internalDragSource is { } src)
        {
            await HandleInternalMoveAsync(src, destDir);
            return;
        }

        // Each of the bail-outs below reports why. They are all indistinguishable to the
        // user otherwise — the drop simply does nothing — which makes an upload that
        // silently fails on one platform impossible to diagnose from a bug report.
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            SetStatus($"Drop ignored — no file data. Offered: {DescribeOfferedFormats(e)}");
            return;
        }

        var storageItems = e.DataTransfer.TryGetFiles()?.ToList();
        if (storageItems == null || storageItems.Count == 0)
        {
            SetStatus(storageItems == null
                ? "Drop failed — the drag source offered files but returned none."
                : "Drop failed — the drag contained an empty file list.");
            return;
        }

        // Resolve to local file system paths
        var localPaths = new List<string>();
        foreach (var item in storageItems)
        {
            var lp = item.Path.IsAbsoluteUri && item.Path.Scheme == "file"
                ? item.Path.LocalPath
                : item.Path.ToString();
            if (!string.IsNullOrWhiteSpace(lp))
                localPaths.Add(lp);
        }
        if (localPaths.Count == 0)
        {
            // Files arrived but none resolved to a local path — e.g. a file manager
            // handing over a non-file URI (trash:/, sftp://, a portal handle).
            var uris = string.Join(", ", storageItems.Select(i => i.Path.ToString()));
            SetStatus($"Drop ignored — no local paths in: {uris}");
            return;
        }

        // Local-only scan: count files and compute size for the confirmation prompt
        var preview = new List<TransferJob>();
        await Task.Run(() =>
        {
            foreach (var lp in localPaths)
                ScanLocalUpload(lp, destDir, preview);
        });

        if (preview.Count == 0)
        {
            SetStatus($"Nothing to upload — could not read: {string.Join(", ", localPaths)}");
            return;
        }

        // Confirmation
        var host = TopLevel.GetTopLevel(this) as Window;
        var confirmed = await ShowUploadConfirmAsync(
            preview.Count, preview.Sum(j => j.TotalBytes), destDir, host);
        if (!confirmed) return;

        // Check write permission
        var canWrite = await Task.Run(() => ProbeWriteAccess(destDir));
        if (!canWrite)
        {
            SetStatus($"Upload failed: no write permission to {destDir}");
            return;
        }

        // Build jobs (creates remote directories as a side-effect)
        var jobs = new List<TransferJob>();
        await Task.Run(() =>
        {
            foreach (var lp in localPaths)
                BuildUploadJobs(lp, destDir, jobs);
        });

        if (jobs.Count == 0) return;
        _transferQueue.Enqueue(jobs);
    }

    /// <summary>
    /// Pure local scan — no remote operations. Builds a job list used only for
    /// counting and size display in the confirmation prompt.
    /// </summary>
    private static void ScanLocalUpload(string localPath, string remoteDir, List<TransferJob> jobs)
    {
        localPath = localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Directory.Exists(localPath))
        {
            var dirName = Path.GetFileName(localPath);
            if (string.IsNullOrEmpty(dirName)) return; // skip drive roots like C:\
            var remoteSubDir = remoteDir.TrimEnd('/') + "/" + dirName;
            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
                ScanLocalUpload(child, remoteSubDir, jobs);
        }
        else if (File.Exists(localPath))
        {
            var info = new FileInfo(localPath);
            jobs.Add(new TransferJob
            {
                IsUpload   = true,
                LocalPath  = localPath,
                RemotePath = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(localPath),
                TotalBytes = info.Length,
            });
        }
    }

    private static async Task<bool> ShowUploadConfirmAsync(
        int fileCount, long totalBytes, string destPath, Window? owner)
    {
        bool confirmed = false;

        var sizeStr = totalBytes switch
        {
            >= 1_073_741_824 => $"{totalBytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576     => $"{totalBytes / 1_048_576.0:F1} MB",
            >= 1_024         => $"{totalBytes / 1_024.0:F1} KB",
            _                => $"{totalBytes} B",
        };

        var okBtn     = new Button { Content = "Upload", Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", IsCancel = true };

        var win = new Window
        {
            Title  = "Confirm upload",
            Width  = 400,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin  = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Upload {fileCount} file{(fileCount != 1 ? "s" : "")} ({sizeStr}) to:\n{destPath}",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
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

        okBtn.Click     += (_, _) => { confirmed = true;  win.Close(); };
        cancelBtn.Click += (_, _) => { confirmed = false; win.Close(); };
        win.Opened      += (_, _) => okBtn.Focus();

        if (owner != null)
            await win.ShowDialog(owner);
        else
            win.Show();

        return confirmed;
    }

    private void BuildUploadJobs(string localPath, string remoteDir, List<TransferJob> jobs)
    {
        // Normalize: strip trailing separators so Path.GetFileName works correctly for directories
        localPath = localPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (Directory.Exists(localPath))
        {
            var dirName = Path.GetFileName(localPath);
            if (string.IsNullOrEmpty(dirName)) return; // skip drive roots like C:\
            var remoteSubDir = remoteDir.TrimEnd('/') + "/" + dirName;
            try { _sftpClient.CreateDirectory(remoteSubDir); } catch { /* already exists */ }

            foreach (var child in Directory.EnumerateFileSystemEntries(localPath))
                BuildUploadJobs(child, remoteSubDir, jobs);
        }
        else if (File.Exists(localPath))
        {
            var info = new FileInfo(localPath);
            jobs.Add(new TransferJob
            {
                IsUpload   = true,
                LocalPath  = localPath,
                RemotePath = remoteDir.TrimEnd('/') + "/" + Path.GetFileName(localPath),
                TotalBytes = info.Length,
            });
        }
    }

    private bool ProbeWriteAccess(string remoteDir)
    {
        var probe = remoteDir.TrimEnd('/') + "/.termthing-probe";
        try
        {
            using var s = _sftpClient.OpenWrite(probe);
            s.Close();
            _sftpClient.DeleteFile(probe);
            return true;
        }
        catch { return false; }
    }

    private SftpEntry? GetEntryUnderNameColumn(Point posInGrid)
    {
        // Name column is index 1 (index 0 is the icon column)
        // We do a simple hit-test: look for a DataGridRow visual under the pointer
        var hit = _filesGrid.GetVisualDescendants()
                            .OfType<DataGridRow>()
                            .FirstOrDefault(r =>
                            {
                                var bounds = r.TranslatePoint(new Point(0, 0), _filesGrid);
                                if (bounds == null) return false;
                                var rect = new Rect(bounds.Value, new Size(_filesGrid.Bounds.Width, r.Bounds.Height));
                                return rect.Contains(posInGrid);
                            });

        if (hit?.DataContext is SftpEntry { IsDirectory: true, IsParentLink: false } entry)
        {
            // Highlight only when hovering the Name column specifically
            // (icon column is ~26 px; Name column follows it at index 1)
            var nameColWidth = _filesGrid.Columns.Count > 1 ? _filesGrid.Columns[1].ActualWidth : 180;
            if (posInGrid.X > 26 && posInGrid.X <= 26 + nameColWidth)
                return entry;
        }

        return null;
    }

    private SftpEntry? GetEntryUnderPointer(Point posInGrid)
    {
        // Same as GetEntryUnderNameColumn but accepts any column position (for drag-out)
        var hit = _filesGrid.GetVisualDescendants()
                            .OfType<DataGridRow>()
                            .FirstOrDefault(r =>
                            {
                                var bounds = r.TranslatePoint(new Point(0, 0), _filesGrid);
                                if (bounds == null) return false;
                                var rect = new Rect(bounds.Value, new Size(_filesGrid.Bounds.Width, r.Bounds.Height));
                                return rect.Contains(posInGrid);
                            });

        if (hit?.DataContext is SftpEntry { IsParentLink: false } entry)
            return entry;

        return null;
    }

    private static string GetParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0) return "/";
        return trimmed[..lastSlash];
    }

    private void SetStatus(string? message)
    {
        if (message is null) { _statusText.IsVisible = false; return; }
        _statusText.Text = message;
        _statusText.IsVisible = true;
    }

    // -----------------------------------------------------------------------
    // Drag-out / Download (SFTP → OS)
    // -----------------------------------------------------------------------

    private const double DragThresholdPx = 5.0;

    private void OnFilesGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Track right-click position for blank-area context menu detection
        var pt = e.GetCurrentPoint(_filesGrid);
        if (pt.Properties.IsRightButtonPressed)
        {
            var pos = e.GetPosition(_filesGrid);
            _contextMenuOnBlankArea = !_filesGrid.GetVisualDescendants()
                .OfType<DataGridRow>()
                .Any(r =>
                {
                    var origin = r.TranslatePoint(new Point(0, 0), _filesGrid);
                    return origin != null &&
                           new Rect(origin.Value, new Size(_filesGrid.Bounds.Width, r.Bounds.Height)).Contains(pos);
                });
            return;
        }

        if (_dragOutInProgress) return;

        _dragOutPending     = null;
        _dragOutPointerArgs = null;
        _clickedSoleSelectedItem = false;

        var point = e.GetCurrentPoint(_filesGrid);
        if (!point.Properties.IsLeftButtonPressed) return;

        var dragPos = e.GetPosition(_filesGrid);
        var entry   = GetEntryUnderPointer(dragPos);
        if (entry == null) return;

        _dragOutPending     = entry;
        _dragOutStartPos    = dragPos;
        _dragOutPointerArgs = e;

        // Capture "user just clicked the row that was already the sole selection"
        // before the DataGrid updates SelectedItems. Bare click only — Shift/Ctrl
        // preserves standard multi-select semantics.
        if (e.KeyModifiers == KeyModifiers.None
            && _filesGrid.SelectedItems is { } sel
            && sel.Count == 1
            && ReferenceEquals(sel[0], entry))
        {
            _clickedSoleSelectedItem = true;
        }
    }

    private void OnFilesGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragOutPending == null || _dragOutInProgress) return;

        var point = e.GetCurrentPoint(_filesGrid);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragOutPending     = null;
            _dragOutPointerArgs = null;
            return;
        }

        var pos  = e.GetPosition(_filesGrid);
        var dx   = pos.X - _dragOutStartPos.X;
        var dy   = pos.Y - _dragOutStartPos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < DragThresholdPx) return;

        // Threshold exceeded — start the drag
        var entry           = _dragOutPending;
        var pressedArgs     = _dragOutPointerArgs;
        _dragOutPending     = null;
        _dragOutPointerArgs = null;
        _dragOutInProgress  = true;

        _ = InitiateDragDownloadAsync(entry, pressedArgs ?? throw new InvalidOperationException("Pressed event args missing"));
    }

    private void OnFilesGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragOutPending     = null;
        _dragOutPointerArgs = null;
        // _dragOutInProgress is reset by InitiateDragDownloadAsync after completion

        // Toggle-off: bare click landed on the already-sole-selected row and the
        // user didn't drag → clear the selection. Skipped while a drag-out is
        // in flight so dragging the only selected file doesn't lose it mid-flight.
        if (_clickedSoleSelectedItem && !_dragOutInProgress)
        {
            _filesGrid.SelectedItems?.Clear();
        }
        _clickedSoleSelectedItem = false;
    }

    private async Task InitiateDragDownloadAsync(SftpEntry entry, PointerPressedEventArgs pointerArgs)
    {
        _internalDragSource = entry;
        try
        {
            // Windows fast path: a virtual-file (delayed-rendering) drag. The file's
            // bytes are pulled from SFTP only when the drop target asks for them — i.e.
            // on DROP — so the app doesn't freeze pre-downloading while the mouse is
            // held. Single files only; directories fall through to the pre-download
            // path below. DoDragDrop blocks (pumping messages) until the drag ends.
            if (OperatingSystem.IsWindows() && !entry.IsDirectory)
            {
                bool started = false;
                try
                {
                    var remotePath = entry.FullPath;
                    started = WindowsFileDrag.TryDrag(entry.Name, entry.Size, () =>
                    {
                        using var ms = new MemoryStream();
                        _sftpClient.DownloadFile(remotePath, ms);
                        return ms.ToArray();
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SFTP] native drag failed, falling back: {ex.Message}");
                    started = false;
                }
                if (started) return;
            }

            SetStatus("Preparing download…");

            // Download to a per-entry temp directory so the OS gets a real local file path
            var sessionTemp = Path.Combine(
                Path.GetTempPath(), "termthing", "dragout",
                Guid.NewGuid().ToString("N"));

            string localPath;
            try
            {
                localPath = await Task.Run(() =>
                {
                    Directory.CreateDirectory(sessionTemp);
                    return DownloadToTempSync(entry, sessionTemp,
                        msg => Dispatcher.UIThread.Post(() => SetStatus(msg)));
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Drag-out download failed: {ex.Message}");
                return;
            }

            SetStatus(null);

            // Verify the pointer is still pressed before handing off to DoDragDropAsync
            var pt = pointerArgs.GetCurrentPoint(_filesGrid);
            if (!pt.Properties.IsLeftButtonPressed)
                return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel is null) return;

            // Directories resolve via TryGetFolderFromPathAsync — TryGetFileFromPathAsync
            // returns null for a folder, which previously made folder drag-out silently
            // do nothing after the tree had already been downloaded to temp.
            IStorageItem? storageItem = entry.IsDirectory
                ? await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(localPath))
                : await topLevel.StorageProvider.TryGetFileFromPathAsync(new Uri(localPath));
            if (storageItem is null) return;

            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateFile(storageItem));
            await DragDrop.DoDragDropAsync(pointerArgs, transfer, DragDropEffects.Copy);
        }
        finally
        {
            _internalDragSource = null;
            _dragOutInProgress = false;
        }
    }

    /// <summary>
    /// Synchronously downloads <paramref name="entry"/> (file or directory tree) into
    /// <paramref name="destDir"/> and returns the path of the created local item.
    /// Must be called on a background thread. <paramref name="onProgress"/> is invoked
    /// with a human-readable status string as each file is fetched (marshal to the UI
    /// thread inside the callback).
    /// </summary>
    private string DownloadToTempSync(SftpEntry entry, string destDir, Action<string>? onProgress)
    {
        var localPath = Path.Combine(destDir, entry.Name);

        if (entry.IsDirectory)
        {
            Directory.CreateDirectory(localPath);
            foreach (var child in _sftpClient.ListDirectory(entry.FullPath))
            {
                if (child.Name == "." || child.Name == "..") continue;
                var childEntry = new SftpEntry
                {
                    IsDirectory = child.IsDirectory,
                    Name        = child.Name,
                    FullPath    = child.FullName,
                    Size        = child.Length,
                };
                DownloadToTempSync(childEntry, localPath, onProgress);
            }
        }
        else
        {
            onProgress?.Invoke($"Preparing “{entry.Name}” for drag…");
            using var fs = File.Create(localPath);
            _sftpClient.DownloadFile(entry.FullPath, fs);
        }

        return localPath;
    }

    // -----------------------------------------------------------------------
    // Bookmarks — data helpers
    // -----------------------------------------------------------------------

    private List<Bookmark> GetBookmarks()
    {
        if (_definition?.Settings is SshSettings ssh)
            return [.. ssh.Bookmarks];
        return [];
    }

    private void SaveBookmarks(List<Bookmark> bookmarks)
    {
        if (_definition == null || _definition.Settings is not SshSettings ssh || _saveConfig == null) return;
        _definition.Settings = ssh with { Bookmarks = bookmarks };
        _saveConfig();
        UpdateBookmarksHeader();
        RebuildBookmarksList();
    }

    private void ActivateBookmark(string absPath, bool syncTerminal)
    {
        NavigateTo(absPath);
        if (syncTerminal && _sendShellCommand != null)
        {
            // Single-quote escape: ' → '\''
            var escaped = absPath.Replace("'", "'\\''");
            _sendShellCommand($"cd '{escaped}'");
        }
    }

    // -----------------------------------------------------------------------
    // Bookmarks — panel UI
    // -----------------------------------------------------------------------

    private void UpdateBookmarksHeader()
    {
        var count = GetBookmarks().Count;
        _bookmarksHeaderText.Text = count == 0 ? "Bookmarks" : $"Bookmarks ({count})";
    }

    private void SetBookmarksExpanded(bool expanded, bool save = true)
    {
        _bookmarksExpanded = expanded;
        _bookmarksArrow.Text = expanded ? "▼" : "►";

        if (expanded)
        {
            _mainContentGrid.RowDefinitions[2].MinHeight = 228; // ~28 header + 200 body
            _mainContentGrid.RowDefinitions[2].Height = new GridLength(Math.Max(_bookmarksBodyHeight, 228));
            _bookmarkSplitter.IsVisible = true;
            _bookmarksBody.IsVisible = true;
        }
        else
        {
            // Preserve current height before collapsing so it restores correctly.
            var current = _mainContentGrid.RowDefinitions[2].ActualHeight;
            if (_bookmarksBody.IsVisible && current > 20)
            {
                _bookmarksBodyHeight = current;
                if (save)
                {
                    SettingsService.Temp.BookmarksHeightPx = _bookmarksBodyHeight;
                    SettingsService.SaveTemp();
                }
            }
            _mainContentGrid.RowDefinitions[2].MinHeight = 0;
            _mainContentGrid.RowDefinitions[2].Height = GridLength.Auto;
            _bookmarkSplitter.IsVisible = false;
            _bookmarksBody.IsVisible = false;
        }

        if (save)
        {
            SettingsService.Temp.BookmarksExpanded = expanded;
            SettingsService.SaveTemp();
        }
    }

    private void OnBookmarksHeaderTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        SetBookmarksExpanded(!_bookmarksExpanded, save: true);
    }

    private void OnBookmarkSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        var height = _mainContentGrid.RowDefinitions[2].ActualHeight;
        if (height > 20)
        {
            _bookmarksBodyHeight = height;
            SettingsService.Temp.BookmarksHeightPx = height;
            SettingsService.SaveTemp();
        }
    }

    private void RebuildBookmarksList()
    {
        _bookmarksListPanel.Children.Clear();
        var bookmarks = GetBookmarks();
        for (int i = 0; i < bookmarks.Count; i++)
            _bookmarksListPanel.Children.Add(BuildBookmarkItem(bookmarks[i], i));
    }

    private Control BuildBookmarkItem(Bookmark bookmark, int index)
    {
        var nameText = new TextBlock
        {
            Text            = bookmark.Name,
            FontSize        = 12,
            TextTrimming    = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(nameText, bookmark.AbsolutePath);

        var contentPanel = new StackPanel { Spacing = 1 };
        contentPanel.Children.Add(nameText);

        if (bookmark.Name != bookmark.AbsolutePath)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text          = bookmark.AbsolutePath,
                FontSize      = 9,
                Foreground    = Brushes.Gray,
                TextTrimming  = TextTrimming.CharacterEllipsis,
            });
        }

        var itemGrid = new Grid();
        itemGrid.Children.Add(contentPanel);

        var border = new Border
        {
            Padding = new Thickness(6, 4),
            Child   = itemGrid,
        };

        // Hover highlight
        border.PointerEntered += (_, _) => border.Background = new SolidColorBrush(Color.FromRgb(50, 50, 60));
        border.PointerExited  += (_, _) => border.Background = null;

        // Double-click: navigate SFTP + inject cd into terminal
        border.DoubleTapped += (_, _) => ActivateBookmark(bookmark.AbsolutePath, syncTerminal: true);

        // Ctrl+Click: navigate SFTP only
        border.Tapped += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                ActivateBookmark(bookmark.AbsolutePath, syncTerminal: false);
        };

        // Right-click context menu
        var cm = new ContextMenu();

        var renameItem = new MenuItem { Header = "Rename…" };
        renameItem.Click += async (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;
            var dialog = new RenameDialog(bookmark.Name);
            var newName = await dialog.ShowDialog<string?>(owner);
            if (!string.IsNullOrWhiteSpace(newName))
            {
                var bm = GetBookmarks();
                if (index < bm.Count)
                {
                    bm[index] = bm[index] with { Name = newName };
                    SaveBookmarks(bm);
                }
            }
        };

        var editPathItem = new MenuItem { Header = "Edit path…" };
        editPathItem.Click += async (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            if (owner == null) return;
            var dialog = new RenameDialog(bookmark.AbsolutePath) { Title = "Edit path" };
            var newPath = await dialog.ShowDialog<string?>(owner);
            if (!string.IsNullOrWhiteSpace(newPath))
            {
                newPath = "/" + newPath.Trim('/');
                var bm = GetBookmarks();
                if (index < bm.Count)
                {
                    bm[index] = bm[index] with { AbsolutePath = newPath };
                    SaveBookmarks(bm);
                }
            }
        };

        var deleteItem = new MenuItem { Header = "Delete" };
        deleteItem.Click += (_, _) =>
        {
            var bm = GetBookmarks();
            bm.RemoveAt(index);
            SaveBookmarks(bm);
        };

        cm.Items.Add(renameItem);
        cm.Items.Add(editPathItem);
        cm.Items.Add(new Separator());
        cm.Items.Add(deleteItem);
        border.ContextMenu = cm;

        return border;
    }

    // -----------------------------------------------------------------------
    // Bookmarks — drag-and-drop reorder (pointer-driven, no Avalonia DragDrop API)
    // -----------------------------------------------------------------------

    private void OnBookmarksPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _bookmarkDragSourceIndex = null;
        _bookmarkDragActive      = false;

        if (!e.GetCurrentPoint(_bookmarksListPanel).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(_bookmarksListPanel);
        var children = _bookmarksListPanel.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child  = children[i];
            var origin = child.TranslatePoint(new Point(0, 0), _bookmarksListPanel);
            if (origin == null) continue;
            if (new Rect(origin.Value, child.Bounds.Size).Contains(pos))
            {
                _bookmarkDragSourceIndex = i;
                _bookmarkDragStartPos    = pos;
                break;
            }
        }
    }

    private void OnBookmarksPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_bookmarkDragSourceIndex.HasValue) return;
        if (!e.GetCurrentPoint(_bookmarksListPanel).Properties.IsLeftButtonPressed)
        {
            _bookmarkDragSourceIndex = null;
            _bookmarkDragActive      = false;
            return;
        }

        var pos = e.GetPosition(_bookmarksListPanel);
        var dx  = pos.X - _bookmarkDragStartPos.X;
        var dy  = pos.Y - _bookmarkDragStartPos.Y;
        if (!_bookmarkDragActive && Math.Sqrt(dx * dx + dy * dy) < DragThresholdPx) return;
        _bookmarkDragActive = true;
    }

    private void OnBookmarksPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_bookmarkDragActive && _bookmarkDragSourceIndex.HasValue)
        {
            var pos         = e.GetPosition(_bookmarksListPanel);
            var targetIndex = ComputeBookmarkDropIndex(pos.Y);
            var sourceIndex = _bookmarkDragSourceIndex.Value;

            if (targetIndex != sourceIndex)
            {
                var bm   = GetBookmarks();
                var item = bm[sourceIndex];
                bm.RemoveAt(sourceIndex);
                var insertAt = Math.Min(targetIndex, bm.Count);
                bm.Insert(insertAt, item);
                SaveBookmarks(bm);
            }
        }

        _bookmarkDragSourceIndex = null;
        _bookmarkDragActive      = false;
    }

    private int ComputeBookmarkDropIndex(double y)
    {
        var children = _bookmarksListPanel.Children;
        for (int i = 0; i < children.Count; i++)
        {
            var child  = children[i];
            var origin = child.TranslatePoint(new Point(0, 0), _bookmarksListPanel);
            if (origin == null) continue;
            if (y < origin.Value.Y + child.Bounds.Height / 2.0)
                return i;
        }
        return children.Count;
    }

    private static string BuildPermissionString(Renci.SshNet.Sftp.ISftpFile f)
    {
        var a = f.Attributes;
        Span<char> s = stackalloc char[10];
        s[0] = f.IsDirectory ? 'd' : f.IsSymbolicLink ? 'l' : '-';
        s[1] = a.OwnerCanRead ? 'r' : '-';
        s[2] = a.OwnerCanWrite ? 'w' : '-';
        s[3] = a.OwnerCanExecute ? 'x' : '-';
        s[4] = a.GroupCanRead ? 'r' : '-';
        s[5] = a.GroupCanWrite ? 'w' : '-';
        s[6] = a.GroupCanExecute ? 'x' : '-';
        s[7] = a.OthersCanRead ? 'r' : '-';
        s[8] = a.OthersCanWrite ? 'w' : '-';
        s[9] = a.OthersCanExecute ? 'x' : '-';
        return new string(s);
    }
}
