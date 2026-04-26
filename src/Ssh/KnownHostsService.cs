using Renci.SshNet.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TermThing.Configuration;

namespace TermThing.Ssh;

/// <summary>
/// File-backed implementation of <see cref="IKnownHostsService"/>.
/// Entries are stored in <c>config/known_hosts.json</c> alongside the exe.
/// Fingerprints are SHA-256 of the raw host-key bytes, base-64 encoded.
/// </summary>
public sealed class KnownHostsService : IKnownHostsService
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<KnownHostEntry> _entries;

    public KnownHostsService()
    {
        _entries = Load();
    }

    public KnownHostStatus Check(string host, int port, HostKeyEventArgs e)
    {
        var fp = Fingerprint(e.HostKey);
        var match = _entries.FirstOrDefault(x =>
            x.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && x.Port == port);

        if (match is null) return KnownHostStatus.Unknown;
        return match.FingerprintSha256 == fp
            ? KnownHostStatus.Trusted
            : KnownHostStatus.Mismatched;
    }

    public void Trust(string host, int port, HostKeyEventArgs e)
    {
        // Remove any existing entry for this host:port (replaces on key rotation)
        _entries.RemoveAll(x =>
            x.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && x.Port == port);

        _entries.Add(new KnownHostEntry
        {
            Host = host,
            Port = port,
            KeyAlgorithm = e.HostKeyName,
            FingerprintSha256 = Fingerprint(e.HostKey),
            AddedUtc = DateTime.UtcNow,
        });

        Save();
    }

    public IReadOnlyList<KnownHostEntry> List() => _entries.AsReadOnly();

    public void Remove(KnownHostEntry entry)
    {
        _entries.Remove(entry);
        Save();
    }

    private static string Fingerprint(byte[] key)
        => Convert.ToBase64String(SHA256.HashData(key));

    private static List<KnownHostEntry> Load()
    {
        var path = AppPaths.KnownHostsFile;
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<KnownHostEntry>>(json, _options) ?? [];
        }
        catch { return []; }
    }

    private void Save()
    {
        var path = AppPaths.KnownHostsFile;
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, _options));
        File.Move(tmp, path, overwrite: true);
    }
}
