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
        var raw = typeof(VersionHelper).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? "0.0.0-dev";

        var s = raw
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
