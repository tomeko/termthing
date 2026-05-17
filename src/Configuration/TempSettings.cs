using TermThing.Sessions;

namespace TermThing.Configuration;

/// <summary>
/// Auto-saved UI-state settings persisted to <c>tempsettings.json</c> next to the
/// executable. These are overwritten frequently and are never imported or exported.
/// </summary>
public sealed class TempSettings
{
    public int Version { get; init; } = 1;

    // -----------------------------------------------------------------------
    // Main window layout
    // -----------------------------------------------------------------------

    /// <summary>Width (pixels) of the left panel (sessions/SFTP column).</summary>
    public double LeftColumnWidthPx { get; set; } = 260;

    /// <summary>Width of the window when last closed.</summary>
    public double WindowWidth { get; set; } = 1200;

    /// <summary>Height of the window when last closed.</summary>
    public double WindowHeight { get; set; } = 750;

    /// <summary>Whether the window was maximized when last closed.</summary>
    public bool WindowMaximized { get; set; } = false;

    /// <summary>Index of the selected left tab (0 = Sessions, 1 = SFTP).</summary>
    public int ActiveLeftTab { get; set; } = 0;

    // -----------------------------------------------------------------------
    // SFTP DataGrid column widths (keyed by column header text)
    // -----------------------------------------------------------------------

    public Dictionary<string, double> SftpColumnWidths { get; set; } = [];

    // -----------------------------------------------------------------------
    // New-session toolbar
    // -----------------------------------------------------------------------

    /// <summary>
    /// The session kind opened by the primary New-Session button.
    /// Remembered across restarts so the user's last choice is pre-selected.
    /// </summary>
    public SessionKind LastNewSessionKind { get; set; } = SessionKind.Local;

    /// <summary>
    /// Ordered list of recently launched saved-session IDs (most-recent first).
    /// Stale IDs (deleted sessions) are pruned on tree changes.
    /// </summary>
    public List<Guid> RecentSessionIds { get; set; } = [];

    // -----------------------------------------------------------------------
    // Terminal
    // -----------------------------------------------------------------------

    /// <summary>
    /// Font size used by terminal tabs. 0 means "use the launcher default".
    /// Adjusted at runtime via Ctrl+Wheel.
    /// </summary>
    public double TerminalFontSize { get; set; } = 0;

    // -----------------------------------------------------------------------
    // Download destinations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Recently used download folders (most-recent first, capped at 5).
    /// </summary>
    public List<string> RecentDownloadFolders { get; set; } = [];

    // -----------------------------------------------------------------------
    // Local session shell picker (Windows)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Process path of the last-selected local shell on Windows
    /// (e.g. <c>"cmd.exe"</c> or <c>"C:\Program Files\PowerShell\7\pwsh.exe"</c>).
    /// Used to pre-select the right item in the shell-picker dropdown.
    /// Empty string means "use the first available option".
    /// </summary>
    public string LastLocalShell { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    // Session tree
    // -----------------------------------------------------------------------

    /// <summary>
    /// Expansion state for session tree groups, keyed by <see cref="Sessions.SessionGroup.Id"/>.
    /// Missing keys default to <c>true</c> (expanded) — ensures auto-expand on first run.
    /// </summary>
    public Dictionary<Guid, bool> SessionTreeExpansion { get; set; } = [];

    // -----------------------------------------------------------------------
    // SFTP Bookmarks panel
    // -----------------------------------------------------------------------

    /// <summary>Whether the bookmarks panel is expanded.</summary>
    public bool BookmarksExpanded { get; set; } = false;

    /// <summary>Height (pixels) of the bookmarks body when expanded.</summary>
    public double BookmarksHeightPx { get; set; } = 160;

    // -----------------------------------------------------------------------
    // Auto-updater
    // -----------------------------------------------------------------------

    /// <summary>
    /// UTC timestamp of the last successful update-check network call (regardless of
    /// whether an update was found). Used to enforce the 24-hour polling interval.
    /// <c>null</c> means no check has been performed yet in this installation.
    /// </summary>
    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>
    /// Tag name of a release the user has explicitly skipped (e.g. <c>"v0.2.0"</c>).
    /// Subsequent background checks silently ignore this exact tag.
    /// The Help → Check for updates… manual trigger always runs regardless.
    /// </summary>
    public string? SkippedUpdateTag { get; set; }
}
