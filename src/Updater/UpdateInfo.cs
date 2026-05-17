namespace TermThing.Updater;

/// <summary>
/// Information about an available update, derived from the GitHub <c>releases/latest</c> response.
/// </summary>
public sealed record UpdateInfo(
    Version Latest,
    string TagName,
    string ReleaseNotesMarkdown,
    Uri HtmlUrl,
    IReadOnlyList<UpdateAsset> Assets);
