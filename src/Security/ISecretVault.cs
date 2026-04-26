namespace TermThing.Security;

/// <summary>
/// Placeholder interface for future cross-platform optional secret encryption.
/// A concrete implementation could use DPAPI on Windows, libsecret on Linux,
/// or the macOS Keychain — all hidden behind this abstraction.
/// The <see cref="NullSecretVault"/> default does nothing (secrets are never persisted).
/// </summary>
public interface ISecretVault
{
    /// <summary>Store a secret. Returns an opaque key that can retrieve it later.</summary>
    Task<string> StoreAsync(string label, string secret);

    /// <summary>Retrieve a previously stored secret by its opaque key.</summary>
    Task<string?> RetrieveAsync(string opaqueKey);

    /// <summary>Delete a stored secret.</summary>
    Task DeleteAsync(string opaqueKey);
}
