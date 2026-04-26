using Avalonia.Controls;
using Renci.SshNet;
using TermThing.Configuration;
using TermThing.Views;

namespace TermThing.Sftp;

/// <summary>
/// Handles the "Open" action for remote files in the SFTP browser.
/// Downloads the file to a session-scoped temp directory, then either launches
/// the configured app (via <see cref="FileAssociation"/>) or prompts the user
/// to choose how to open it.
/// </summary>
public sealed class SftpFileOpener
{
    private readonly SftpClient _sftp;
    private readonly Guid _sessionId;
    private readonly Window _hostWindow;

    public SftpFileOpener(SftpClient sftp, Guid sessionId, Window hostWindow)
    {
        _sftp       = sftp;
        _sessionId  = sessionId;
        _hostWindow = hostWindow;
    }

    /// <summary>
    /// Downloads <paramref name="remotePath"/> to a temp directory and opens it
    /// with the configured app (or prompts the user if none is registered).
    /// </summary>
    public async Task OpenAsync(string remotePath)
    {
        var fileName  = Path.GetFileName(remotePath);
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // Check for a registered association
        var assoc = SettingsService.App.FileAssociations
            .FirstOrDefault(a => string.Equals(a.Extension, extension, StringComparison.OrdinalIgnoreCase));

        if (assoc != null)
        {
            // Known association — download then launch immediately
            var localPath = await DownloadToTempAsync(remotePath, fileName);
            OsFileLauncher.LaunchWith(assoc.AppPath, assoc.Args, localPath);
            return;
        }

        // No association — show the custom prompt
        var dialog = new OpenWithDialog(fileName);
        await dialog.ShowDialog(_hostWindow);

        switch (dialog.Result)
        {
            case OpenWithResult.SetNew:
                // Open settings window pre-focused on the File Associations tab,
                // with a new row pre-filled for this extension.
                var settings = new SettingsWindow();
                settings.OpenToFileAssociation(extension);
                await settings.ShowDialog(_hostWindow);
                // After the user configures and commits, retry
                if (settings.Committed)
                    await OpenAsync(remotePath);
                break;

            case OpenWithResult.UseOs:
                var tempPath = await DownloadToTempAsync(remotePath, fileName);
                OsFileLauncher.OpenWithOsDialog(tempPath);
                break;

            // case OpenWithResult.None: user cancelled — do nothing
        }
    }

    // -----------------------------------------------------------------------

    private async Task<string> DownloadToTempAsync(string remotePath, string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), "termthing", _sessionId.ToString("N"));
        Directory.CreateDirectory(dir);

        var localPath = Path.Combine(dir, fileName);

        await Task.Run(() =>
        {
            using var fs = File.Create(localPath);
            _sftp.DownloadFile(remotePath, fs);
        });

        return localPath;
    }
}
