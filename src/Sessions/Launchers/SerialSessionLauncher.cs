namespace TermThing.Sessions.Launchers;

/// <summary>
/// Groundwork-only launcher for serial sessions.
/// The session-type infrastructure (kind, settings, dialog, registry entry) is
/// fully wired; the actual connection is not yet implemented.
/// </summary>
public sealed class SerialSessionLauncher : ISessionLauncher
{
    public SessionKind Kind => SessionKind.Serial;

    public Task<ISessionInstance> LaunchAsync(
        SessionDefinition definition,
        ISessionPromptHost promptHost,
        CancellationToken cancellationToken = default)
    {
        // TODO: implement serial (System.IO.Ports / cross-platform PTY adapter)
        throw new NotImplementedException(
            "Serial sessions are not yet implemented. " +
            "This is a groundwork placeholder — the connection dialog, session " +
            "definition, and registry entry are all in place.");
    }
}
