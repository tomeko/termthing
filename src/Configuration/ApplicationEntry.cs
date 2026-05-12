namespace TermThing.Configuration;

/// <summary>Kind of application entry used in the Open With feature.</summary>
public enum ApplicationKind { External, TermThingEditor }

/// <summary>
/// An application that can open remote files in the SFTP browser.
/// Supersedes the simpler <see cref="FileAssociation"/> model.
/// </summary>
public sealed class ApplicationEntry
{
    /// <summary>Well-known ID that identifies the built-in TermThing text editor.</summary>
    public static readonly Guid BuiltInEditorId = new("00000000-0000-0000-0000-000000000001");

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name shown in the Open With submenu.</summary>
    public string Name { get; set; } = string.Empty;

    public ApplicationKind Kind { get; set; } = ApplicationKind.External;

    /// <summary>Full path to the executable. Ignored when <see cref="Kind"/> is <see cref="ApplicationKind.TermThingEditor"/>.</summary>
    public string AppPath { get; set; } = string.Empty;

    /// <summary>Optional argument template; use {file} as a placeholder for the local file path.</summary>
    public string Args { get; set; } = string.Empty;

    /// <summary>
    /// Lower-case extensions this app handles, e.g. ".log", ".txt".
    /// An empty list means the entry matches any extension.
    /// </summary>
    public List<string> Extensions { get; set; } = [];

    /// <summary>Whether this is the default app when double-clicking a file with a matching extension.</summary>
    public bool IsDefault { get; set; }
}
