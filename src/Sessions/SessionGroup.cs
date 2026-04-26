using System.Collections.ObjectModel;

namespace TermThing.Sessions;

/// <summary>
/// A named group that can hold session definitions and nested subgroups.
/// The root group ("All Sessions") is the entry point for the sessions tree.
/// </summary>
public sealed class SessionGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Group";
    public ObservableCollection<SessionDefinition> Sessions { get; init; } = [];
    public ObservableCollection<SessionGroup> Subgroups { get; init; } = [];

    /// <summary>Creates the default root group used when no config file exists.</summary>
    public static SessionGroup CreateRoot() => new() { Name = "All Sessions" };

    /// <summary>Returns a deep clone with fresh GUIDs (for import de-collision).</summary>
    public SessionGroup DeepClone()
    {
        var clone = new SessionGroup { Name = Name };
        foreach (var s in Sessions)
            clone.Sessions.Add(s.Duplicate());
        foreach (var g in Subgroups)
            clone.Subgroups.Add(g.DeepClone());
        return clone;
    }
}
