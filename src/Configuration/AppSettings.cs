namespace TermThing.Configuration;

/// <summary>
/// User-curated application settings persisted to <c>appsettings.json</c> next to the
/// executable. These settings are never included in session import/export.
/// </summary>
public sealed class AppSettings
{
    public int Version { get; init; } = 1;

    /// <summary>
    /// Number of recent sessions shown at the bottom of the New-Session dropdown.
    /// Range: 1-50.
    /// </summary>
    public int RecentSessionsCount { get; set; } = 10;

    /// <summary>
    /// Legacy file-extension-to-application associations. Kept only for one-shot
    /// migration into <see cref="Applications"/>; not used directly after migration.
    /// </summary>
    public List<FileAssociation> FileAssociations { get; set; } = [];

    /// <summary>
    /// Application entries used by the SFTP browser's Open With feature.
    /// Supersedes <see cref="FileAssociations"/>.
    /// </summary>
    public List<ApplicationEntry> Applications { get; set; } = [];

    /// <summary>
    /// Whether to show a confirmation dialog when closing the main window
    /// while one or more session tabs (or unsaved SFTP editors) are open.
    /// Toggleable from the confirmation dialog itself or the Settings window.
    /// </summary>
    public bool ConfirmExitWithOpenSessions { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, TermThing silently polls GitHub Releases in the background
    /// (at most once every 24 hours) and shows an update prompt when a newer version
    /// is available. Set to <c>false</c> to disable all automatic checks; the
    /// Help → Check for updates… menu item always works regardless.
    /// </summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>
    /// When <c>true</c>, right-click paste in the terminal sends the clipboard
    /// contents directly without showing a confirmation dialog. Default is
    /// <c>false</c> (confirm every paste). Per-session override lives on the
    /// session's <c>SessionSettings.SkipPasteConfirmation</c>.
    /// </summary>
    public bool SkipPasteConfirmation { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, paste-to-terminal does not show the extra "this contains
    /// a newline — it will execute" warning. Independent from
    /// <see cref="SkipPasteConfirmation"/>: the newline guard is a stronger
    /// safety net so a user can keep it on even after silencing the regular
    /// confirm dialog. Default <c>false</c> (warn on newline pastes).
    /// </summary>
    public bool SkipNewlinePasteConfirmation { get; set; } = false;

    /// <summary>
    /// Set after the first launch finishes its one-time onboarding (currently:
    /// the OpenSSH config import prompt). Persisted so the prompt does not return
    /// on subsequent launches even if the user declined the import.
    /// </summary>
    public bool FirstRunCompleted { get; set; } = false;

    /// <summary>
    /// When <c>true</c>, imported OpenSSH-config groups are kept in the config
    /// (and still auto-refreshed) but hidden from the sessions tree. Lets users
    /// keep the import wired up without cluttering their tree.
    /// </summary>
    public bool HideImportedSshConfig { get; set; } = false;

    /// <summary>
    /// One-shot migration: converts old <see cref="FileAssociations"/> entries into
    /// <see cref="Applications"/> entries and clears the source list.
    /// </summary>
    public void MigrateFromFileAssociations()
    {
        if (Applications.Count > 0 || FileAssociations.Count == 0) return;
        foreach (var fa in FileAssociations)
        {
            var name = string.IsNullOrWhiteSpace(fa.AppPath)
                ? fa.Extension
                : Path.GetFileNameWithoutExtension(fa.AppPath);
            Applications.Add(new ApplicationEntry
            {
                Name       = string.IsNullOrWhiteSpace(name) ? fa.Extension : name,
                Kind       = ApplicationKind.External,
                AppPath    = fa.AppPath,
                Args       = fa.Args,
                Extensions = [fa.Extension.ToLowerInvariant()],
                IsDefault  = true,
            });
        }
        FileAssociations.Clear();
    }
}
