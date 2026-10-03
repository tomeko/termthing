using System.Text.Json;
using System.Text.Json.Serialization;
using TermThing.Diagnostics;

namespace TermThing.Configuration;

/// <summary>
/// Generic atomic JSON load/save helper used for <see cref="AppSettings"/> and
/// <see cref="TempSettings"/>. Writes via a temp file to avoid truncation on crash.
/// </summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T LoadOrDefault<T>(string filePath) where T : new()
    {
        if (!File.Exists(filePath))
            return new T();

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json, _options) ?? new T();
        }
        catch (Exception ex)
        {
            // Corrupted file — keep a copy so the next save doesn't destroy it, then start fresh
            var backup = BackupCorrupt(filePath);
            Log.Error("settings", $"Could not read {filePath}; starting fresh (backup: {backup ?? "none"})", ex);
            return new T();
        }
    }

    public static void Save<T>(T value, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = filePath + ".tmp";
        var json = JsonSerializer.Serialize(value, _options);
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>Copies an unreadable settings file aside as <c>&lt;name&gt;.bad-&lt;timestamp&gt;</c>.</summary>
    internal static string? BackupCorrupt(string filePath)
    {
        try
        {
            var backup = $"{filePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(filePath, backup, overwrite: true);
            return backup;
        }
        catch { return null; }
    }
}
