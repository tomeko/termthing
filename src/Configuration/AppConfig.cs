using TermThing.Sessions;

namespace TermThing.Configuration;

/// <summary>
/// Preserved for one-shot migration: if an old termthing.json contains UiPreferences,
/// we copy them into TempSettings on first load then ignore them going forward.
/// </summary>
public sealed class LegacyUiPreferences
{
    public double LeftColumnWidth { get; set; } = 0;
    public int ActiveLeftTab { get; set; } = 0;
    public double WindowWidth { get; set; } = 0;
    public double WindowHeight { get; set; } = 0;
}

public sealed class AppConfig
{
    public int Version { get; init; } = 1;
    public SessionGroup RootGroup { get; set; } = SessionGroup.CreateRoot();

    /// <summary>Legacy field — only read during the one-shot migration.</summary>
    public LegacyUiPreferences? UiPreferences { get; set; }
}
