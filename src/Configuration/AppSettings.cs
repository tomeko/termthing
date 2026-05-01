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
    /// File-extension-to-application associations used by the SFTP file opener.
    /// Keyed by lower-case extension (including the dot), e.g. ".txt".
    /// </summary>
    public List<FileAssociation> FileAssociations { get; set; } = [];

    /// <summary>
    /// Whether to show a confirmation dialog when closing the main window
    /// while one or more session tabs (or unsaved SFTP editors) are open.
    /// Toggleable from the confirmation dialog itself or the Settings window.
    /// </summary>
    public bool ConfirmExitWithOpenSessions { get; set; } = true;

    // Future: terminal prefs (font family, colour theme, etc.) will live here.
}
