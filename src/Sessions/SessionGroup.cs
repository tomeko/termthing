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

    /// <summary>
    /// When <c>true</c>, the tree UI disables mutation operations (edit/rename/delete/drop)
    /// on this group and its sessions. Used for imported groups whose contents are
    /// regenerated from an external source (e.g. OpenSSH config).
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Origin tag for imported groups (e.g. <c>"ssh-config"</c>). <c>null</c> for user-created groups.
    /// </summary>
    public string? OriginKind { get; set; }

    /// <summary>
    /// Absolute path to the source the group was generated from (e.g. <c>~/.ssh/config</c>).
    /// Used by the refresh action to re-read and rebuild the group.
    /// </summary>
    public string? SourcePath { get; set; }

    /// <summary>Creates the default root group used when no config file exists.</summary>
    public static SessionGroup CreateRoot() => new() { Name = "All Sessions" };

    /// <summary>Returns a deep clone with fresh GUIDs (for import de-collision).</summary>
    public SessionGroup DeepClone()
    {
        var clone = new SessionGroup
        {
            Name       = Name,
            IsReadOnly = IsReadOnly,
            OriginKind = OriginKind,
            SourcePath = SourcePath,
        };
        foreach (var s in Sessions)
            clone.Sessions.Add(s.Clone());
        foreach (var g in Subgroups)
            clone.Subgroups.Add(g.DeepClone());
        return clone;
    }
}
