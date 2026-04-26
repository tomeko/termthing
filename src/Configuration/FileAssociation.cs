namespace TermThing.Configuration;

/// <summary>
/// Maps a file extension (lower-case, with leading dot) to an external application.
/// </summary>
public sealed class FileAssociation
{
    /// <summary>Lower-case extension including the leading dot, e.g. ".txt".</summary>
    public string Extension { get; set; } = string.Empty;

    /// <summary>Full path to the executable used to open files with this extension.</summary>
    public string AppPath { get; set; } = string.Empty;

    /// <summary>
    /// Optional argument template passed to the app. Use {file} as a placeholder for
    /// the local file path, e.g. "--file={file}". When empty the file path is passed as
    /// the sole argument.
    /// </summary>
    public string Args { get; set; } = string.Empty;
}
