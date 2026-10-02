using System.Reflection;

namespace TermThing.Updater;

/// <summary>
/// Helpers for reading and comparing version numbers embedded in the assembly.
/// </summary>
public static class VersionHelper
{
    /// <summary>
    /// Returns the current assembly version, or <c>null</c> when running from a dev build
    /// (<c>0.0.0-dev</c>).  Callers should skip update checks when this returns <c>null</c>.
    /// </summary>
    public static Version? CurrentVersion()
    {
        var s = RawInformationalVersion()
            .Split('+')[0]   // strip "+gitsha" suffix injected by the SDK
            .Split('-')[0];  // strip "-dev", "-rc1", etc.

        if (!Version.TryParse(s, out var v))
            return null;

        // Treat 0.0.0 as "dev build — skip update checks"
        if (v.Major == 0 && v.Minor == 0 && v.Build <= 0)
            return null;

        return v;
    }

    /// <summary>
    /// Human-readable version for the UI: <c>"0.2.0"</c> for releases, <c>"dev"</c> for
    /// development builds.
    /// </summary>
    public static string DisplayVersion() =>
        CurrentVersion() is { } v ? Format(v) : "dev";

    /// <summary>
    /// The short (7-char) commit SHA the SDK appends to the informational version
    /// (<c>"0.2.0+&lt;sha&gt;"</c>), or <c>null</c> when the build has none.
    /// </summary>
    public static string? CommitSha()
    {
        var raw  = RawInformationalVersion();
        var plus = raw.IndexOf('+');
        if (plus < 0) return null;

        var sha = raw[(plus + 1)..];
        return sha.Length > 7 ? sha[..7] : sha.Length > 0 ? sha : null;
    }

    /// <summary>Formats a version as <c>major.minor.build</c> (no trailing <c>.revision</c>).</summary>
    public static string Format(Version v) =>
        v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    private static string RawInformationalVersion() =>
        typeof(VersionHelper).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0-dev";

    /// <summary>
    /// Parses a GitHub tag name (e.g. <c>"v1.2.3"</c> or <c>"1.2.3-rc1"</c>) into a
    /// <see cref="Version"/>, stripping any leading <c>v</c> and any pre-release suffix.
    /// Returns <c>null</c> if the tag cannot be parsed.
    /// </summary>
    public static Version? ParseTag(string tag)
    {
        var s = tag.StartsWith('v') ? tag[1..] : tag;

        var dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];

        return Version.TryParse(s, out var v) ? v : null;
    }
}
