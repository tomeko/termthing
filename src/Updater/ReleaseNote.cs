using System.Text.RegularExpressions;

namespace TermThing.Updater;

/// <summary>
/// One published GitHub release's notes. <see cref="Markdown"/> is already cleaned
/// of the install boilerplate the release workflow adds (see <see cref="Clean"/>).
/// </summary>
public sealed record ReleaseNote(
    Version Version,
    string TagName,
    DateTimeOffset? PublishedAt,
    string Markdown,
    Uri HtmlUrl)
{
    // Must match the markers in .github/workflows/release.yml. Everything between
    // them (macOS quarantine / SmartScreen notes) is for first-time downloaders,
    // not for someone who already has the app running.
    private const string InstallNotesStart = "<!-- install-notes:start -->";
    private const string InstallNotesEnd   = "<!-- install-notes:end -->";

    // generate_release_notes always appends this compare link.
    private static readonly Regex FullChangelogLine =
        new(@"^\s*\*\*Full Changelog\*\*:.*$", RegexOptions.Multiline);

    /// <summary>
    /// Strips the workflow's install-notes block and the "Full Changelog" line
    /// from a raw release body.
    /// </summary>
    public static string Clean(string body)
    {
        var s = body.Replace("\r\n", "\n");

        var start = s.IndexOf(InstallNotesStart, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = s.IndexOf(InstallNotesEnd, start, StringComparison.Ordinal);
            s = end >= 0
                ? s[..start] + s[(end + InstallNotesEnd.Length)..]
                : s[..start];
        }

        s = FullChangelogLine.Replace(s, string.Empty);
        return s.Trim();
    }
}
