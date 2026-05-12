namespace TermThing.Configuration;

/// <summary>
/// Singleton accessor for <see cref="TempSettings"/> and <see cref="AppSettings"/>.
/// Loaded once at startup; callers use <see cref="Temp"/> and <see cref="App"/> directly.
/// </summary>
public static class SettingsService
{
    public static TempSettings Temp { get; private set; } = new();
    public static AppSettings App { get; private set; } = new();

    public static void Load()
    {
        App  = SettingsStore.LoadOrDefault<AppSettings>(AppPaths.AppSettingsFile);
        Temp = SettingsStore.LoadOrDefault<TempSettings>(AppPaths.TempSettingsFile);
        App.MigrateFromFileAssociations();
    }

    public static void SaveTemp() =>
        SettingsStore.Save(Temp, AppPaths.TempSettingsFile);

    public static void SaveApp() =>
        SettingsStore.Save(App, AppPaths.AppSettingsFile);
}
