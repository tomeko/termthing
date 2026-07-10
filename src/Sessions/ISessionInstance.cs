using System.Threading.Tasks;
using Avalonia.Controls;
using Iciclecreek.Terminal;

namespace TermThing.Sessions;

/// <summary>
/// Represents a running terminal session (tab content + lifecycle).
/// Returned by <see cref="ISessionLauncher.LaunchAsync"/>.
/// </summary>
public interface ISessionInstance : IDisposable
{
    /// <summary>The control to place as the tab's Content. May be a wrapper panel hosting <see cref="Terminal"/>.</summary>
    Control TabContent { get; }

    /// <summary>The actual <see cref="TerminalControl"/> inside <see cref="TabContent"/> (null when the session has no terminal).</summary>
    TerminalControl? Terminal { get; }

    /// <summary>Optional SFTP browser panel (null when not applicable or not currently open).</summary>
    Control? SftpPanel { get; }

    /// <summary>True when this session can open an SFTP browser on demand after connecting.</summary>
    bool CanUseSftp => false;

    /// <summary>True when the SFTP browser is currently open.</summary>
    bool IsSftpActive => SftpPanel != null;

    /// <summary>
    /// Opens (<paramref name="on"/> = true) or closes the SFTP browser after the
    /// session has connected. Returns true on success. Default: not supported.
    /// </summary>
    Task<bool> SetSftpEnabledAsync(bool on) => Task.FromResult(false);

    /// <summary>
    /// Raised (on the UI thread) when <see cref="SftpPanel"/> appears or disappears —
    /// e.g. the user opened/closed SFTP via <see cref="SetSftpEnabledAsync"/>.
    /// Default: no-op (sessions that never change their panel).
    /// </summary>
    event EventHandler? SftpPanelChanged { add { } remove { } }

    /// <summary>Current display title (may change after connection via OSC title).</summary>
    string Title { get; }

    /// <summary>
    /// Raised when the underlying process/connection terminates.
    /// Fired on the UI thread.
    /// </summary>
    event EventHandler? SessionEnded;

    /// <summary>Kill/disconnect the session immediately.</summary>
    void Kill();
}
