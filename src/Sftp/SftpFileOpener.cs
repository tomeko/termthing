using Avalonia.Controls;
using Renci.SshNet;
using TermThing.Configuration;
using TermThing.Editor;
using TermThing.Views;

namespace TermThing.Sftp;

/// <summary>
/// Handles the "Open" action for remote files in the SFTP browser.
///
/// Decision tree for double-click (<see cref="OpenAsync"/>):
/// 1. A default <see cref="ApplicationEntry"/> lists the extension → open with that app.
/// 2. Otherwise <see cref="FileOpenPolicy"/> decides: media/documents → OS default app;
///    executables, disk images and oversized files → nothing; text → a catch-all
///    default app if one is set, else the built-in text editor.
///
/// Explicit pick (<see cref="OpenWithEntryAsync"/>) routes directly to the chosen entry.
/// </summary>
public sealed class SftpFileOpener
{
    private readonly SftpClient _sftp;
    private readonly Guid _sessionEditorId;
    private readonly string _displayHost;
    private readonly Window _hostWindow;
    private readonly EditorRegistry? _editors;

    public SftpFileOpener(
        SftpClient sftp,
        Guid sessionEditorId,
        string displayHost,
        Window hostWindow,
        EditorRegistry? editors = null)
    {
        _sftp            = sftp;
        _sessionEditorId = sessionEditorId;
        _displayHost     = displayHost;
        _hostWindow      = hostWindow;
        _editors         = editors;
    }

    /// <summary>
    /// Opens <paramref name="remotePath"/> the way a double-click should. Returns null when
    /// the file was opened, or a message explaining why it deliberately was not.
    /// </summary>
    public async Task<string?> OpenAsync(string remotePath, long size)
    {
        var fileName  = Path.GetFileName(remotePath);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // 1. A default application the user configured for THIS extension always wins.
        var explicitDefault = SettingsService.App.Applications
            .FirstOrDefault(a => a.IsDefault && a.Extensions.Contains(extension));
        if (explicitDefault != null)
        {
            await OpenWithEntryAsync(remotePath, explicitDefault);
            return null;
        }

        switch (FileOpenPolicy.ForDoubleClick(extension, size))
        {
            case DoubleClickAction.None:
                return $"Not opened automatically — use Open With to open \u201c{fileName}\u201d.";

            case DoubleClickAction.Native:
            {
                var localPath = await DownloadToTempAsync(remotePath, fileName);
                OsFileLauncher.OpenWithDefaultApp(localPath);
                return null;
            }
        }

        // 2. Text. A catch-all default (no extensions listed) applies only here — it used
        //    to win for every file, sending images and binaries to the text editor.
        var catchAllDefault = SettingsService.App.Applications
            .FirstOrDefault(a => a.IsDefault && a.Extensions.Count == 0);
        if (catchAllDefault != null)
        {
            await OpenWithEntryAsync(remotePath, catchAllDefault);
            return null;
        }

        if (_editors != null)
        {
            await OpenInBuiltInEditorAsync(remotePath, fileName);
            return null;
        }

        // Fallback: OS handler
        var fallbackPath = await DownloadToTempAsync(remotePath, fileName);
        OsFileLauncher.OpenWithOsDialog(fallbackPath);
        return null;
    }

    /// <summary>
    /// Opens <paramref name="remotePath"/> with the explicitly chosen <paramref name="entry"/>.
    /// </summary>
    public async Task OpenWithEntryAsync(string remotePath, ApplicationEntry entry)
    {
        var fileName = Path.GetFileName(remotePath);

        if (entry.Kind == ApplicationKind.TermThingEditor)
        {
            if (_editors != null)
                await OpenInBuiltInEditorAsync(remotePath, fileName);
            return;
        }

        var localPath = await DownloadToTempAsync(remotePath, fileName);
        OsFileLauncher.LaunchWith(entry.AppPath, entry.Args, localPath);
    }

    /// <summary>
    /// Downloads <paramref name="remotePath"/> and shows the OS "Open with" chooser for it.
    /// </summary>
    public async Task OpenWithOsChooserAsync(string remotePath)
    {
        var localPath = await DownloadToTempAsync(remotePath, Path.GetFileName(remotePath));
        OsFileLauncher.OpenWithOsDialog(localPath);
    }

    // -----------------------------------------------------------------------
    // Built-in editor path
    // -----------------------------------------------------------------------

    private async Task OpenInBuiltInEditorAsync(string remotePath, string fileName)
    {
        var localPath = await DownloadToTempAsync(remotePath, fileName);
        var bytes     = await File.ReadAllBytesAsync(localPath);
        var text      = DecodeText(bytes);

        var ctx = new TextEditorContext
        {
            SessionId     = _sessionEditorId,
            RemotePath    = remotePath,
            LocalTempPath = localPath,
            DisplayHost   = _displayHost,
            UploadAsync   = MakeUploadDelegate(remotePath),
        };

        await _editors!.OpenOrFocusAsync(ctx, text);
    }

    private Func<byte[], CancellationToken, Task> MakeUploadDelegate(string remotePath)
    {
        var sftp = _sftp;
        return (bytes, ct) => Task.Run(() =>
        {
            using var stream = new MemoryStream(bytes);
            sftp.UploadFile(stream, remotePath, canOverride: true);
        }, ct);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async Task<string> DownloadToTempAsync(string remotePath, string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "termthing", _sessionEditorId.ToString("N"));
        Directory.CreateDirectory(dir);

        var localPath = Path.Combine(dir, fileName);

        await Task.Run(() =>
        {
            using var fs = File.Create(localPath);
            _sftp.DownloadFile(remotePath, fs);
        });

        return localPath;
    }

    /// <summary>
    /// Decodes raw bytes to a string, honouring UTF-8 / UTF-16 BOMs and
    /// falling back to UTF-8 without BOM.
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
