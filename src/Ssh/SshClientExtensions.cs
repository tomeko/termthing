using Renci.SshNet;

namespace TermThing.Ssh;

public static class SshClientExtensions
{
    /// <summary>
    /// <see cref="BaseClient.IsConnected"/> that answers <c>false</c> for a disposed (or
    /// null) client instead of throwing. Session teardown disposes clients while timers,
    /// pane-exit handlers and pending SFTP listings may still be checking them.
    /// </summary>
    public static bool IsAlive(this BaseClient? client)
    {
        if (client is null) return false;
        try { return client.IsConnected; }
        catch (ObjectDisposedException) { return false; }
    }
}
