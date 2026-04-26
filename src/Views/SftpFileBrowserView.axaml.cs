using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Renci.SshNet;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using TermThing.Configuration;
using TermThing.Sftp;

namespace TermThing.Views;

public sealed class SftpEntry
{
    public bool IsDirectory { get; init; }
    public bool IsParentLink { get; init; }
    public string Name { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string Permissions { get; init; } = string.Empty;
    public int OwnerId { get; init; }
    public int GroupId { get; init; }

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

    // Drag-out state — removed: Avalonia DoDragDrop requires a live pointer event,
    // which is gone after an async file download. Use "Download…" context menu instead.

    // Drop-highlight state
    private SftpEntry? _dropTargetDir; // null = whole view

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

        _filesGrid.ItemsSource = Entries;

        // Wire transfer queue to the progress overlay
        _transferQueue = new TransferQueue(_sftpClient);
        _transferOverlay.Bind(_transferQueue);
        _transferQueue.TransferError += (_, msg) =>
            Dispatcher.UIThread.Post(() => SetStatus($"Transfer error: {msg}"));
        _transferQueue.QueueEmpty += (_, _) =>
            Dispatcher.UIThread.Post(Refresh);

        // Set up drag/drop
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent,  OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent,      OnDrop);

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

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Handled = true;
#pragma warning disable CS0618
        if (!e.Data.Contains(DataFormats.Files))
#pragma warning restore CS0618
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;

        _dropTargetDir = null;
        var rowEntry = GetEntryUnderNameColumn(e.GetPosition(_filesGrid));
        if (rowEntry != null)
            _dropTargetDir = rowEntry;
    }

    private void OnDragLeave(object? sender, RoutedEventArgs e)
    {
        _dropTargetDir = null;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var destDir = _dropTargetDir?.FullPath ?? _currentPath;
        _dropTargetDir = null;

#pragma warning disable CS0618
        if (!e.Data.Contains(DataFormats.Files)) return;
        var files = e.Data.GetFiles();
#pragma warning restore CS0618
        if (files == null) return;

        var canWrite = await Task.Run(() => ProbeWriteAccess(destDir));
        if (!canWrite)
        {
            SetStatus($"Upload failed: no write permission to {destDir}");
            return;
        }

        var jobs = new List<TransferJob>();
        foreach (var file in files)
        {
            var localPath = file.Path.IsAbsoluteUri && file.Path.Scheme == "file"
                ? file.Path.LocalPath
                : file.Path.ToString();
            if (string.IsNullOrWhiteSpace(localPath)) continue;

            BuildUploadJobs(localPath, destDir, jobs);
        }

        if (jobs.Count == 0) return;

        _transferQueue.Enqueue(jobs);
    }

    private void BuildUploadJobs(string localPath, string remoteDir, List<TransferJob> jobs)
    {
        if (Directory.Exists(localPath))
        {
            var dirName = Path.GetFileName(localPath);
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
            // Only highlight when the pointer is roughly over the Name column
            // (column 1 starts after the icon column ~26 px wide)
            if (posInGrid.X > 26)
                return entry;
        }

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
