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
