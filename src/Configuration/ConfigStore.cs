using System.Text.Json;
using System.Text.Json.Serialization;
using TermThing.Sessions;

namespace TermThing.Configuration;

public static class ConfigStore
{
    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig LoadOrDefault()
    {
        var path = AppPaths.SessionsFile;
        if (!File.Exists(path))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppConfig>(json, _options) ?? new AppConfig();
        }
        catch
        {
            // Corrupted config — start fresh
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        var path = AppPaths.SessionsFile;
        var tmp = path + ".tmp";

        var json = JsonSerializer.Serialize(config, _options);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
