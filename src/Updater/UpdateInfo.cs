namespace TermThing.Updater;

/// <summary>
/// Information about an available update, derived from the GitHub releases list.
/// </summary>
/// <param name="Notes">
/// Notes for every release newer than the running version, up to and including
/// <paramref name="Latest"/>, newest first — so users who skipped versions see
/// everything they missed.
/// </param>
public sealed record UpdateInfo(
    Version Latest,
    string TagName,
    IReadOnlyList<ReleaseNote> Notes,
    Uri HtmlUrl,
    IReadOnlyList<UpdateAsset> Assets);
