using Renci.SshNet.Common;
using TermThing.Ssh;
using TermThing.Views;

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
    /// Shows a minimal password-only prompt when connecting to a host that requires
    /// password authentication and no key file is configured.
    /// Returns the password string, or <c>null</c> if the user cancelled.
    /// </summary>
    Task<string?> PromptForPasswordAsync(string username, string host);

    /// <summary>
    /// Shows a minimal passphrase-only prompt for an encrypted private key.
    /// <paramref name="hostname"/> is shown in the dialog title when provided.
    /// Returns the passphrase string, or <c>null</c> if the user cancelled.
    /// </summary>
    Task<string?> PromptForPassphraseAsync(string keyFilePath, string? hostname = null);

    /// <summary>
    /// Shows the host-key trust dialog and returns the user's action.
    /// Routing this through <c>MainWindow</c> (rather than resolving the window via
    /// <c>Application.Current</c>) ensures correct modal ownership so that no
    /// spurious empty window appears behind the dialog.
    /// </summary>
    Task<HostKeyAction> PromptHostKeyAsync(string host, int port, KnownHostStatus status, HostKeyEventArgs args);

    /// <summary>
    /// Shows a non-blocking error dialog. Used by launchers to surface pre-flight
    /// failures (e.g. host unreachable) without throwing an exception that would
    /// produce a second error notification.
    /// </summary>
    Task ShowErrorAsync(string title, string message);
}
