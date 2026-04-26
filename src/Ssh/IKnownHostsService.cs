using Renci.SshNet.Common;

namespace TermThing.Ssh;

public enum KnownHostStatus { Trusted, Unknown, Mismatched }

/// <summary>
/// Manages the persistent list of trusted SSH host keys.
/// </summary>
public interface IKnownHostsService
{
    /// <summary>Check whether the host key in <paramref name="e"/> is trusted.</summary>
    KnownHostStatus Check(string host, int port, HostKeyEventArgs e);

    /// <summary>Persist the host key as trusted.</summary>
    void Trust(string host, int port, HostKeyEventArgs e);

    /// <summary>Return all stored entries.</summary>
    IReadOnlyList<KnownHostEntry> List();

    /// <summary>Remove a stored entry.</summary>
    void Remove(KnownHostEntry entry);
}
