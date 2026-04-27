using TermThing.Configuration;
using TermThing.Sessions;

namespace TermThing.Ssh;

/// <summary>
/// Resolves the <see cref="JumpHost"/> list from an <see cref="SshSettings"/> into an
/// ordered sequence of <see cref="ResolvedHop"/> records, performing cycle-detection and
/// referencing any named sessions from <see cref="AppConfig"/>.
/// </summary>
internal static class JumpHostResolver
{
    private const int MaxHops = 8;

    /// <summary>
    /// Resolves the jump-host chain for <paramref name="target"/>.
    /// Returns an empty list when no jump hosts are configured (direct connect).
    /// </summary>
    /// <exception cref="JumpHostConfigurationException">
    /// Thrown when a referenced session cannot be found, the chain contains a cycle,
    /// or the chain exceeds <see cref="MaxHops"/> hops.
    /// </exception>
    public static IReadOnlyList<ResolvedHop> Resolve(SshSettings target, AppConfig config)
    {
        if (target.JumpHosts.Count == 0)
            return [];

        if (target.JumpHosts.Count > MaxHops)
            throw new JumpHostConfigurationException(
                $"Jump-host chain is too long ({target.JumpHosts.Count} hops; maximum is {MaxHops}).");

        var resolved = new List<ResolvedHop>(target.JumpHosts.Count);

        // Track (host:port:user) tuples to detect duplicate hops.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Track session Ids to detect circular session references.
        var seenIds = new HashSet<Guid>();

        for (int i = 0; i < target.JumpHosts.Count; i++)
        {
            var hop = target.JumpHosts[i];
            ResolvedHop rh;

            if (hop.Kind == JumpHostKind.SessionRef)
            {
                if (hop.SessionId is null)
                    throw new JumpHostConfigurationException(
                        $"Jump hop {i + 1} is a session reference but has no SessionId.");

                if (!seenIds.Add(hop.SessionId.Value))
                    throw new JumpHostConfigurationException(
                        $"Cycle detected: session {hop.SessionId} appears more than once in the jump-host chain.");

                var def = FindSession(hop.SessionId.Value, config.RootGroup)
                    ?? throw new JumpHostConfigurationException(
                        $"Jump hop {i + 1} references session {hop.SessionId} which no longer exists.");

                if (def.Kind != SessionKind.Ssh || def.Settings is not SshSettings refSettings)
                    throw new JumpHostConfigurationException(
                        $"Jump hop {i + 1} references session '{def.Name}' which is not an SSH session.");

                rh = new ResolvedHop(
                    LogicalHost : refSettings.Host,
                    LogicalPort : refSettings.Port,
                    Username    : refSettings.Username,
                    KeyFilePath : refSettings.KeyFilePath,
                    Source      : def,
                    JumpHop     : hop);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(hop.Host))
                    throw new JumpHostConfigurationException(
                        $"Jump hop {i + 1} is inline but has no host configured.");

                rh = new ResolvedHop(
                    LogicalHost : hop.Host,
                    LogicalPort : hop.Port,
                    Username    : hop.Username,
                    KeyFilePath : hop.KeyFilePath,
                    Source      : null,
                    JumpHop     : hop);
            }

            // Duplicate-hop detection (same logical endpoint+user)
            var key = $"{rh.LogicalHost}:{rh.LogicalPort}:{rh.Username}";
            if (!seen.Add(key))
                throw new JumpHostConfigurationException(
                    $"Duplicate hop detected: {rh.LogicalHost}:{rh.LogicalPort} (user '{rh.Username}') appears more than once in the chain.");

            // Also ensure the hop doesn't target the same endpoint as the final target.
            var targetKey = $"{target.Host}:{target.Port}:{target.Username}";
            if (string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
                throw new JumpHostConfigurationException(
                    $"Jump hop {i + 1} targets the same host/user as the destination, which would cause a loop.");

            resolved.Add(rh);
        }

        return resolved;
    }

    private static SessionDefinition? FindSession(Guid id, SessionGroup group)
    {
        foreach (var s in group.Sessions)
            if (s.Id == id) return s;
        foreach (var sub in group.Subgroups)
        {
            var found = FindSession(id, sub);
            if (found is not null) return found;
        }
        return null;
    }
}

// ---------------------------------------------------------------------------

/// <summary>A fully-resolved hop with the effective logical host/port and auth details.</summary>
/// <param name="LogicalHost">
/// The real hostname of this hop — used for <c>KnownHostsService</c> lookups even when the
/// TCP connection goes through a loopback port-forward.
/// </param>
/// <param name="Source">
/// The <see cref="SessionDefinition"/> this hop was resolved from, or <c>null</c> for inline hops.
/// Used to show the session name in progress/error messages.
/// </param>
/// <param name="JumpHop">The original <see cref="JumpHost"/> object (carries transient secrets).</param>
internal sealed record ResolvedHop(
    string             LogicalHost,
    int                LogicalPort,
    string             Username,
    string?            KeyFilePath,
    SessionDefinition? Source,
    JumpHost           JumpHop)
{
    /// <summary>Human-readable label for status/error messages.</summary>
    public string DisplayName => Source?.Name
        ?? (string.IsNullOrWhiteSpace(Username)
            ? LogicalHost
            : $"{Username}@{LogicalHost}:{LogicalPort}");

    /// <summary>Current transient password from the underlying JumpHop.</summary>
    public string? TransientPassword
    {
        get => JumpHop.TransientPassword
            ?? (Source?.Settings as SshSettings)?.TransientPassword;
        set
        {
            JumpHop.TransientPassword = value;
            if (Source?.Settings is SshSettings s) s.TransientPassword = value;
        }
    }

    /// <summary>Current transient key passphrase from the underlying JumpHop.</summary>
    public string? TransientKeyPassphrase
    {
        get => JumpHop.TransientKeyPassphrase
            ?? (Source?.Settings as SshSettings)?.TransientKeyPassphrase;
        set
        {
            JumpHop.TransientKeyPassphrase = value;
            if (Source?.Settings is SshSettings s) s.TransientKeyPassphrase = value;
        }
    }
}

// ---------------------------------------------------------------------------

/// <summary>Thrown when the jump-host chain is misconfigured.</summary>
public sealed class JumpHostConfigurationException(string message) : Exception(message);
