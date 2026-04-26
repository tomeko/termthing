namespace TermThing.Sessions;

/// <summary>
/// Creates a running <see cref="ISessionInstance"/> from a <see cref="SessionDefinition"/>.
/// One implementation per <see cref="SessionKind"/>.
/// </summary>
public interface ISessionLauncher
{
    SessionKind Kind { get; }

    Task<ISessionInstance> LaunchAsync(
        SessionDefinition definition,
        ISessionPromptHost promptHost,
        CancellationToken cancellationToken = default);
}
