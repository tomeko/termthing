using TermThing.Sessions;

namespace TermThing.Ssh;

/// <summary>Outcome of one <see cref="SshConfigSync.Apply"/> call.</summary>
public sealed record SshConfigSyncResult(int Added, int Updated, int Unchanged)
{
    public int Total => Added + Updated + Unchanged;
}

/// <summary>
/// One-way import of OpenSSH config hosts into the session tree as ordinary,
/// editable sessions. Each imported session remembers its Host alias
/// (<see cref="SessionDefinition.ImportKey"/>) so later syncs update it in place —
/// wherever the user has moved or renamed it — instead of adding a duplicate.
/// Only connection fields that come from the config are overwritten; everything
/// else (name, icon, bookmarks, tmux, sysmon …) belongs to the user. Sessions are
/// never deleted by a sync.
/// </summary>
public static class SshConfigSync
{
    public const string OriginKind = "ssh-config";
    private const string LandingGroupName = "SSH config";

    /// <summary>
    /// Adds or updates a session for each host. New sessions go into the existing
    /// ssh-config folder (preferring one created from <paramref name="sourcePath"/>),
    /// or into a new "SSH config" folder under <paramref name="root"/>.
    /// </summary>
    public static SshConfigSyncResult Apply(SessionGroup root, string sourcePath, IReadOnlyList<ParsedSshHost> hosts)
    {
        var byKey = new Dictionary<string, List<SessionDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in AllSessions(root))
        {
            if (s.ImportSource != OriginKind || string.IsNullOrEmpty(s.ImportKey)) continue;
            if (!byKey.TryGetValue(s.ImportKey, out var list)) byKey[s.ImportKey] = list = [];
            list.Add(s);
        }

        int added = 0, updated = 0, unchanged = 0;
        SessionGroup? landing = null;

        foreach (var host in hosts)
        {
            var incoming = SshConfigImporter.ToSettings(host);

            if (byKey.TryGetValue(host.Alias, out var existing))
            {
                bool changed = false;
                foreach (var def in existing)
                {
                    var current = def.Settings as SshSettings ?? new SshSettings();
                    if (SameConnection(current, incoming)) continue;
                    def.Settings = current with
                    {
                        Host         = incoming.Host,
                        Port         = incoming.Port,
                        Username     = incoming.Username,
                        KeyFilePath  = incoming.KeyFilePath,
                        ProxyCommand = incoming.ProxyCommand,
                        JumpHosts    = incoming.JumpHosts,
                    };
                    changed = true;
                }
                if (changed) updated++; else unchanged++;
                continue;
            }

            landing ??= FindOrCreateLandingGroup(root, sourcePath);
            var created = new SessionDefinition
            {
                Name         = host.Alias,
                Kind         = SessionKind.Ssh,
                Settings     = incoming,
                ImportSource = OriginKind,
                ImportKey    = host.Alias,
            };
            landing.Sessions.Add(created);
            byKey[host.Alias] = [created]; // a later duplicate alias in the same run updates, not re-adds
            added++;
        }

        return new SshConfigSyncResult(added, updated, unchanged);
    }

    /// <summary>
    /// Converts groups produced by the old read-only importer into ordinary editable
    /// groups and tags their sessions for alias matching. The legacy importer named
    /// each session after its Host alias, so the current name is the key. Safe to run
    /// repeatedly. Returns true when anything changed.
    /// </summary>
    public static bool MigrateLegacyGroups(SessionGroup group)
    {
        bool changed = false;
        if (group.OriginKind == OriginKind && group.IsReadOnly)
        {
            group.IsReadOnly = false;
            foreach (var s in AllSessions(group))
            {
                if (s.ImportSource is not null || s.Kind != SessionKind.Ssh) continue;
                s.ImportSource = OriginKind;
                s.ImportKey    = s.Name;
            }
            changed = true;
        }
        foreach (var sub in group.Subgroups)
            changed |= MigrateLegacyGroups(sub);
        return changed;
    }

    private static bool SameConnection(SshSettings a, SshSettings b)
        => a.Host == b.Host
        && a.Port == b.Port
        && a.Username == b.Username
        && (a.KeyFilePath ?? "") == (b.KeyFilePath ?? "")
        && (a.ProxyCommand ?? "") == (b.ProxyCommand ?? "")
        && a.JumpHosts.Count == b.JumpHosts.Count
        && a.JumpHosts.Zip(b.JumpHosts).All(p =>
               p.First.Kind == p.Second.Kind
            && p.First.Host == p.Second.Host
            && p.First.Port == p.Second.Port
            && p.First.Username == p.Second.Username);

    private static SessionGroup FindOrCreateLandingGroup(SessionGroup root, string sourcePath)
    {
        var groups = AllGroups(root).Where(g => g.OriginKind == OriginKind).ToList();
        var match = groups.FirstOrDefault(g => PathsEqual(g.SourcePath, sourcePath)) ?? groups.FirstOrDefault();
        if (match is not null) return match;

        var created = new SessionGroup { Name = LandingGroupName, OriginKind = OriginKind, SourcePath = sourcePath };
        root.Subgroups.Add(created);
        return created;
    }

    private static bool PathsEqual(string? a, string? b)
        => a is not null && b is not null
        && string.Equals(a, b, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<SessionGroup> AllGroups(SessionGroup group)
    {
        yield return group;
        foreach (var sub in group.Subgroups)
            foreach (var g in AllGroups(sub))
                yield return g;
    }

    private static IEnumerable<SessionDefinition> AllSessions(SessionGroup group)
        => AllGroups(group).SelectMany(g => g.Sessions);
}
