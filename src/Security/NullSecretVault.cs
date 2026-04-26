namespace TermThing.Security;

/// <summary>
/// Default no-op implementation — secrets are never persisted.
/// Replace with a platform-aware implementation once the feature is needed.
/// </summary>
public sealed class NullSecretVault : ISecretVault
{
    public Task<string> StoreAsync(string label, string secret)
        => Task.FromResult(string.Empty);

    public Task<string?> RetrieveAsync(string opaqueKey)
        => Task.FromResult<string?>(null);

    public Task DeleteAsync(string opaqueKey)
        => Task.CompletedTask;
}
