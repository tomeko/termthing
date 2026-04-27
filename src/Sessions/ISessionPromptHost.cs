namespace TermThing.Sessions;

/// <summary>
/// Implemented by <c>MainWindow</c> so launchers can request runtime secrets
/// (e.g. SSH password/passphrase) via dialogs without storing them.
/// </summary>
public interface ISessionPromptHost
{
    /// <summary>
    /// Called by <c>SshSessionLauncher</c> when the definition has no transient
    /// secret fields set.  The implementation should show a dialog and populate
    /// <see cref="SshSettings.TransientPassword"/> /
    /// <see cref="SshSettings.TransientKeyPassphrase"/> on the definition's settings.
    /// Returns <c>true</c> if the user confirmed, <c>false</c> to abort.
    /// </summary>
    Task<bool> PromptForSshSecretsAsync(SessionDefinition definition);

    /// <summary>
    /// Shows a minimal passphrase-only prompt for an encrypted private key.
    /// <paramref name="hostname"/> is shown in the dialog title when provided.
    /// Returns the passphrase string, or <c>null</c> if the user cancelled.
    /// </summary>
    Task<string?> PromptForPassphraseAsync(string keyFilePath, string? hostname = null);
}
