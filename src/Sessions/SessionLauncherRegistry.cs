namespace TermThing.Sessions;

/// <summary>
/// Keyed registry of <see cref="ISessionLauncher"/> implementations.
/// Populated once in <c>MainWindow</c> and shared read-only thereafter.
/// </summary>
public sealed class SessionLauncherRegistry
{
    private readonly Dictionary<SessionKind, ISessionLauncher> _launchers = new();

    public void Register(ISessionLauncher launcher)
        => _launchers[launcher.Kind] = launcher;

    public ISessionLauncher Get(SessionKind kind)
    {
        if (_launchers.TryGetValue(kind, out var launcher))
            return launcher;
        throw new InvalidOperationException($"No launcher registered for session kind '{kind}'.");
    }
}
