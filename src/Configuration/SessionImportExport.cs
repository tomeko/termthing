using System.Text.Json;
using System.Text.Json.Serialization;
using TermThing.Sessions;

namespace TermThing.Configuration;

public static class SessionImportExport
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Exports a single group (and all descendants) to a JSON file.
    /// Secrets are excluded automatically via <c>[JsonIgnore]</c> on <see cref="SshSettings"/>.
    /// </summary>
    public static void Export(SessionGroup group, string filePath)
    {
        var json = JsonSerializer.Serialize(group, _options);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Imports a group from a JSON file, assigning fresh GUIDs to all items
    /// to avoid collisions with existing sessions/groups.
    /// </summary>
    public static SessionGroup Import(string filePath)
    {
        var json = File.ReadAllText(filePath);
        var raw = JsonSerializer.Deserialize<SessionGroup>(json, _options)
            ?? throw new InvalidOperationException("Failed to parse imported file.");
        return raw.DeepClone();
    }

    /// <summary>
    /// Walks <paramref name="group"/> and clears any <c>KeyFilePath</c> values that
    /// do not exist on the current machine (e.g. Windows paths imported on Linux).
    /// Returns one entry per cleared path: <c>(session or hop name, original path)</c>.
    /// </summary>
    public static List<(string OwnerName, string BadPath)> SanitizeKeyPaths(SessionGroup group)
    {
        var cleared = new List<(string, string)>();
        SanitizeGroup(group, cleared);
        return cleared;
    }

    // -------------------------------------------------------------------------

    private static void SanitizeGroup(SessionGroup group, List<(string, string)> cleared)
    {
        foreach (var session in group.Sessions)
            SanitizeSession(session, cleared);
        foreach (var sub in group.Subgroups)
            SanitizeGroup(sub, cleared);
    }

    private static void SanitizeSession(SessionDefinition session, List<(string, string)> cleared)
    {
        if (session.Settings is not SshSettings ssh) return;

        // Check the session's own key file
        if (!string.IsNullOrWhiteSpace(ssh.KeyFilePath) && !File.Exists(ssh.KeyFilePath))
        {
            cleared.Add((session.Name, ssh.KeyFilePath));
            session.Settings = ssh with { KeyFilePath = null };
            ssh = (SshSettings)session.Settings; // refresh after mutation
        }

        // Check inline jump-hop key files
        foreach (var hop in ssh.JumpHosts)
        {
            if (!string.IsNullOrWhiteSpace(hop.KeyFilePath) && !File.Exists(hop.KeyFilePath))
            {
                cleared.Add(($"{session.Name} → {hop.Host}", hop.KeyFilePath));
                hop.KeyFilePath = null;
            }
        }
    }
}
