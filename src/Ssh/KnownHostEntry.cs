namespace TermThing.Ssh;

/// <summary>
/// A single trusted host-key entry stored in <c>known_hosts.json</c>.
/// </summary>
public sealed class KnownHostEntry
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string KeyAlgorithm { get; init; } = string.Empty;
    /// <summary>SHA-256 fingerprint of the host key, base-64 encoded.</summary>
    public string FingerprintSha256 { get; init; } = string.Empty;
    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
}
