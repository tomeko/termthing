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
}
