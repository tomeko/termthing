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

    /// <summary>Optional SFTP browser panel (null when not applicable).</summary>
    Control? SftpPanel { get; }

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
