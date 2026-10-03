namespace TermThing.Configuration;

/// <summary>
/// Resolves portable file paths relative to the executable directory.
/// All paths are cross-platform (forward-slash agnostic via <see cref="Path.Combine"/>).
/// </summary>
public static class AppPaths
{
    private static readonly string _configDir =
        Path.Combine(AppContext.BaseDirectory, "config");

    public static string ConfigDirectory
    {
        get
        {
            Directory.CreateDirectory(_configDir);
            return _configDir;
        }
    }

    /// <summary>Daily application log files (see <c>Diagnostics.Log</c>).</summary>
    public static string LogsDirectory
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SessionsFile =>
        Path.Combine(ConfigDirectory, "termthing.json");

    public static string KnownHostsFile =>
        Path.Combine(ConfigDirectory, "known_hosts.json");

    /// <summary>User-curated app settings (file associations, recent count, etc.).</summary>
    public static string AppSettingsFile =>
        Path.Combine(ConfigDirectory, "appsettings.json");

    /// <summary>Auto-saved UI-state (column widths, recent sessions, last new-session kind).</summary>
    public static string TempSettingsFile =>
        Path.Combine(ConfigDirectory, "tempsettings.json");
}
