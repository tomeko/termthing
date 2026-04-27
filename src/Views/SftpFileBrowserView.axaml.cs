using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TermThing.Configuration;
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
    private SshClient? _sshClient;
    private string _currentPath = "/";
    private bool _navigating;

    // Per-session delete confirmation suppression
    private bool _suppressDeleteConfirmThisSession;

    private TextBox _pathBox = null!;
    private Button _refreshButton = null!;
    private DataGrid _filesGrid = null!;
    private CheckBox _followLocationCheckBox = null!;
    private TextBlock _statusText = null!;
    private MenuItem _menuOpen = null!;
    private MenuItem _menuDownloadTo = null!;
    private MenuItem _menuDelete = null!;
    private MenuItem _menuProperties = null!;
    private TransferProgressOverlay _transferOverlay = null!;
    private Border _dropOverlay = null!;

    // Upload drag state
    private SftpEntry? _dropTargetDir;  // folder row being hovered (null = whole view)

    // Drag-to-download state
    private SftpEntry? _dragOutPending;  // entry the user started dragging
    private Point _dragOutStartPos;
    private PointerEventArgs? _dragOutPointerArgs;
    private bool _dragOutInProgress;

    private string? _lastTerminalDir;

    public ObservableCollection<SftpEntry> Entries { get; } = new();
    public string CurrentPath => _currentPath;
    public bool FollowLocation => _followLocationCheckBox?.IsChecked == true;

    public SftpFileBrowserView(SftpClient sftpClient, SshClient? sshClient = null)
    {
        _sftpClient = sftpClient ?? throw new ArgumentNullException(nameof(sftpClient));
        _sshClient  = sshClient;
        InitializeComponent();

        _pathBox               = this.FindControl<TextBox>("PathBox")!;
        _refreshButton         = this.FindControl<Button>("RefreshButton")!;
        _filesGrid             = this.FindControl<DataGrid>("FilesGrid")!;
        _followLocationCheckBox = this.FindControl<CheckBox>("FollowLocationCheckBox")!;
        _statusText            = this.FindControl<TextBlock>("StatusText")!;
        _menuOpen       = this.FindControl<MenuItem>("MenuOpen")!;
        _menuDownloadTo = this.FindControl<MenuItem>("MenuDownloadTo")!;
        _menuDelete     = this.FindControl<MenuItem>("MenuDelete")!;
        _menuProperties = this.FindControl<MenuItem>("MenuProperties")!;
        _transferOverlay       = this.FindControl<TransferProgressOverlay>("TransferOverlay")!;
        _dropOverlay           = this.FindControl<Border>("DropOverlay")!;

        _filesGrid.ItemsSource = Entries;

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

        // Restore persisted column widths
        RestoreColumnWidths();

        // Persist column widths when they change (debounced via a timer per column)
        SubscribeColumnWidthPersistence();
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

        if (!FollowLocation || string.IsNullOrWhiteSpace(path)) return;
        NavigateTo(path);
    }

    private void OnFollowLocationChecked(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_lastTerminalDir))
            NavigateTo(_lastTerminalDir);
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e) => Refresh();

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
    // Context menu
    // -----------------------------------------------------------------------

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var entry = _filesGrid.SelectedItem as SftpEntry;
        bool hasEntry   = entry != null;
        bool isParent   = entry?.IsParentLink == true;
        bool isFile     = hasEntry && !entry!.IsDirectory;
        bool isDir      = hasEntry && entry!.IsDirectory && !isParent;

        _menuOpen.IsEnabled       = isFile && !isParent;
        _menuDownloadTo.IsEnabled = hasEntry && !isParent;
        _menuDelete.IsEnabled     = hasEntry && !isParent;
        _menuProperties.IsVisible = isDir && _sshClient?.IsConnected == true;

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

    private async void OnMenuDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_filesGrid.SelectedItem is not SftpEntry entry || entry.IsParentLink) return;

        if (!_suppressDeleteConfirmThisSession)
        {
            var host = TopLevel.GetTopLevel(this) as Window;
            var confirmed = await ShowDeleteConfirmAsync(entry.Name, entry.IsDirectory, host);
            if (confirmed == null) return; // cancelled
            if (confirmed.Value.SuppressFuture) _suppressDeleteConfirmThisSession = true;
            if (!confirmed.Value.Confirmed) return;
        }

        await Task.Run(() =>
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

        var yesBtn = new Button { Content = "Delete", Margin = new Thickness(0, 0, 8, 0) };
        var noBtn  = new Button { Content = "Cancel" };

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
        win.Closing  += (_, _) => { }; // allow

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
        var opener = new SftpFileOpener(_sftpClient, Guid.NewGuid(), host);
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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
#pragma warning disable CS0618
        if (!e.Data.Contains(DataFormats.Files))
#pragma warning restore CS0618
        {
            e.DragEffects = DragDropEffects.None;
            ClearUploadDragState();
            return;
        }

        e.DragEffects = DragDropEffects.Copy;

        var rowEntry = GetEntryUnderNameColumn(e.GetPosition(_filesGrid));
        SetDropHighlight(rowEntry);

        // Show whole-grid overlay only when not hovering a specific folder
        _dropOverlay.IsVisible = (rowEntry == null);
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

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var destDir = _dropTargetDir?.FullPath ?? _currentPath;
        ClearUploadDragState();

#pragma warning disable CS0618
        if (!e.Data.Contains(DataFormats.Files)) return;
        var storageItems = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618
        if (storageItems == null || storageItems.Count == 0) return;

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
        if (localPaths.Count == 0) return;

        // Local-only scan: count files and compute size for the confirmation prompt
        var preview = new List<TransferJob>();
        await Task.Run(() =>
        {
            foreach (var lp in localPaths)
                ScanLocalUpload(lp, destDir, preview);
        });

        if (preview.Count == 0) return;

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

        var okBtn     = new Button { Content = "Upload", Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel" };

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
        if (_dragOutInProgress) return;

        _dragOutPending     = null;
        _dragOutPointerArgs = null;

        var point = e.GetCurrentPoint(_filesGrid);
        if (!point.Properties.IsLeftButtonPressed) return;

        var pos   = e.GetPosition(_filesGrid);
        var entry = GetEntryUnderPointer(pos);
        if (entry == null) return;

        _dragOutPending     = entry;
        _dragOutStartPos    = pos;
        _dragOutPointerArgs = e;
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
        _dragOutPending     = null;
        _dragOutPointerArgs = null;
        _dragOutInProgress  = true;

        _ = InitiateDragDownloadAsync(entry, e);
    }

    private void OnFilesGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragOutPending     = null;
        _dragOutPointerArgs = null;
        // _dragOutInProgress is reset by InitiateDragDownloadAsync after completion
    }

    private async Task InitiateDragDownloadAsync(SftpEntry entry, PointerEventArgs pointerArgs)
    {
        try
        {
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
                    return DownloadToTempSync(entry, sessionTemp);
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Drag-out download failed: {ex.Message}");
                return;
            }

            SetStatus(null);

            // Verify the pointer is still pressed before handing off to DoDragDrop
            var pt = pointerArgs.GetCurrentPoint(_filesGrid);
            if (!pt.Properties.IsLeftButtonPressed)
                return;

            var dataObj = new DataObject();
#pragma warning disable CS0618
            dataObj.Set(DataFormats.FileNames, new[] { localPath });
            await DragDrop.DoDragDrop(pointerArgs, dataObj, DragDropEffects.Copy);
#pragma warning restore CS0618
        }
        finally
        {
            _dragOutInProgress = false;
        }
    }

    /// <summary>
    /// Synchronously downloads <paramref name="entry"/> (file or directory tree) into
    /// <paramref name="destDir"/> and returns the path of the created local item.
    /// Must be called on a background thread.
    /// </summary>
    private string DownloadToTempSync(SftpEntry entry, string destDir)
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
                DownloadToTempSync(childEntry, localPath);
            }
        }
        else
        {
            using var fs = File.Create(localPath);
            _sftpClient.DownloadFile(entry.FullPath, fs);
        }

        return localPath;
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
