using Avalonia.Controls;
using Renci.SshNet;
using TermThing.Configuration;
using TermThing.Editor;
using TermThing.Views;

namespace TermThing.Sftp;

/// <summary>
/// Handles the "Open" action for remote files in the SFTP browser.
///
/// Decision tree:
/// 1. A matching <see cref="FileAssociation"/> exists → external app (existing behaviour).
/// 2. Extension is in the binary list → show <see cref="OpenWithDialog"/> (existing behaviour).
/// 3. Otherwise → open in the built-in text editor via <see cref="EditorRegistry"/>.
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
    /// Opens <paramref name="remotePath"/>, routing to the external app, the
    /// OS open-with dialog, or the built-in text editor as appropriate.
    /// </summary>
    public async Task OpenAsync(string remotePath)
    {
        var fileName  = Path.GetFileName(remotePath);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // ── 1. Registered file association → external app ──────────────────
        var assoc = SettingsService.App.FileAssociations
            .FirstOrDefault(a => string.Equals(a.Extension, extension, StringComparison.OrdinalIgnoreCase));

        if (assoc != null)
        {
            var localPath = await DownloadToTempAsync(remotePath, fileName);
            OsFileLauncher.LaunchWith(assoc.AppPath, assoc.Args, localPath);
            return;
        }

        // ── 2. Known binary extension → OS open-with dialog ─────────────────
        if (BinaryExtensions.IsLikelyBinary(extension))
        {
            await ShowOpenWithDialogAsync(remotePath, fileName, extension);
            return;
        }

        // ── 3. Text file → built-in editor ──────────────────────────────────
        if (_editors != null)
        {
            await OpenInBuiltInEditorAsync(remotePath, fileName);
            return;
        }

        // Fallback when no EditorRegistry is available (shouldn't happen in normal usage)
        await ShowOpenWithDialogAsync(remotePath, fileName, extension);
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
    // OpenWithDialog fallback
    // -----------------------------------------------------------------------

    private async Task ShowOpenWithDialogAsync(string remotePath, string fileName, string extension)
    {
        var dialog = new OpenWithDialog(fileName);
        await dialog.ShowDialog(_hostWindow);

        switch (dialog.Result)
        {
            case OpenWithResult.SetNew:
                var settings = new SettingsWindow();
                settings.OpenToFileAssociation(extension);
                await settings.ShowDialog(_hostWindow);
                if (settings.Committed)
                    await OpenAsync(remotePath);
                break;

            case OpenWithResult.UseOs:
                var tempPath = await DownloadToTempAsync(remotePath, fileName);
                OsFileLauncher.OpenWithOsDialog(tempPath);
                break;

            // OpenWithResult.None: user cancelled — do nothing
        }
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
