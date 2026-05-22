using System.Text.RegularExpressions;
using TermThing.Sessions;

namespace TermThing.Ssh;

/// <summary>
/// One literal Host block, after wildcard-defaults have been merged in. Holds
/// only the directives TermThing knows how to round-trip into <see cref="SshSettings"/>.
/// </summary>
public sealed class ParsedSshHost
{
    /// <summary>The literal name from the <c>Host</c> line (used as the session name).</summary>
    public string Alias { get; set; } = string.Empty;
    public string? HostName { get; set; }
    public string? User { get; set; }
    public int? Port { get; set; }
    public string? IdentityFile { get; set; }
    public string? ProxyJump { get; set; }
    public string? ProxyCommand { get; set; }
}

/// <summary>
/// Discovers, parses, and imports OpenSSH client config files. Supported
/// directives (per the v1 scope): <c>Host</c>, <c>HostName</c>, <c>User</c>,
/// <c>Port</c>, <c>IdentityFile</c>, <c>ProxyJump</c>, <c>ProxyCommand</c>,
/// <c>Include</c>. Wildcard <c>Host</c> blocks are merged as defaults into
/// matching literal blocks (standard OpenSSH first-match-wins semantics) and
/// then dropped — only literal hosts become sessions.
/// </summary>
public static class SshConfigImporter
{
    private static readonly Regex KeyValueRegex =
        new(@"^\s*([A-Za-z][A-Za-z0-9]*)\s+(.*?)\s*$", RegexOptions.Compiled);

    // -----------------------------------------------------------------------
    // Discovery
    // -----------------------------------------------------------------------

    /// <summary>Returns the platform-default OpenSSH config paths that exist on disk.</summary>
    public static IReadOnlyList<string> Discover()
    {
        var found = new List<string>();
        foreach (var p in Candidates())
        {
            try { if (File.Exists(p)) found.Add(p); } catch { }
        }
        return found;
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".ssh", "config");

        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetEnvironmentVariable("PROGRAMDATA");
            if (!string.IsNullOrWhiteSpace(programData))
                yield return Path.Combine(programData, "ssh", "ssh_config");
        }
        else
        {
            yield return "/etc/ssh/ssh_config";
        }
    }

    // -----------------------------------------------------------------------
    // Parse
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses one config file into a list of resolved literal hosts. Wildcard
    /// blocks (patterns containing <c>* ? !</c> or with multiple patterns) are
    /// merged into matching literal hosts as defaults and not returned themselves.
    /// </summary>
    public static List<ParsedSshHost> Parse(string filePath, HashSet<string>? visited = null)
    {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var literal = new List<ParsedSshHost>();
        var wildcard = new List<(string[] Patterns, ParsedSshHost Defaults)>();
        ParseInto(filePath, literal, wildcard, visited);

        // Merge wildcard defaults into matching literal hosts, in file order
        // (first match wins for each key — only fill missing values).
        foreach (var (patterns, defaults) in wildcard)
        {
            foreach (var host in literal)
            {
                if (!MatchesAny(host.Alias, patterns)) continue;
                host.HostName     ??= defaults.HostName;
                host.User         ??= defaults.User;
                host.Port         ??= defaults.Port;
                host.IdentityFile ??= defaults.IdentityFile;
                host.ProxyJump    ??= defaults.ProxyJump;
                host.ProxyCommand ??= defaults.ProxyCommand;
            }
        }

        return literal;
    }

    private static void ParseInto(
        string filePath,
        List<ParsedSshHost> literal,
        List<(string[] Patterns, ParsedSshHost Defaults)> wildcard,
        HashSet<string> visited)
    {
        var canonical = TryGetCanonical(filePath);
        if (canonical is null || !visited.Add(canonical)) return;

        string[] lines;
        try { lines = File.ReadAllLines(filePath); }
        catch { return; }

        ParsedSshHost? currentLiteral = null;
        ParsedSshHost? currentWildcard = null;
        string[]? currentPatterns = null;

        void Flush()
        {
            if (currentLiteral is not null)
            {
                literal.Add(currentLiteral);
                currentLiteral = null;
            }
            if (currentWildcard is not null && currentPatterns is not null)
            {
                wildcard.Add((currentPatterns, currentWildcard));
                currentWildcard = null;
                currentPatterns = null;
            }
        }

        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            var m = KeyValueRegex.Match(line);
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value.Trim();

            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var patterns = value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (patterns.Length == 0) continue;

                if (patterns.Length > 1 || patterns.Any(IsWildcardPattern))
                {
                    currentWildcard = new ParsedSshHost { Alias = string.Join(" ", patterns) };
                    currentPatterns = patterns;
                }
                else
                {
                    currentLiteral = new ParsedSshHost { Alias = patterns[0] };
                }
                continue;
            }

            if (key.Equals("Include", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var includedPath in ExpandInclude(value, filePath))
                    ParseInto(includedPath, literal, wildcard, visited);
                continue;
            }

            var target = currentLiteral ?? currentWildcard;
            if (target is null) continue;  // Match/global block we don't model — ignore.

            ApplyDirective(target, key, value);
        }

        Flush();
    }

    private static void ApplyDirective(ParsedSshHost host, string key, string value)
    {
        switch (key.ToLowerInvariant())
        {
            case "hostname":      host.HostName     ??= value; break;
            case "user":          host.User         ??= value; break;
            case "port":          if (host.Port is null && int.TryParse(value, out var p)) host.Port = p; break;
            case "identityfile":  host.IdentityFile ??= ExpandHome(StripQuotes(value)); break;
            case "proxyjump":     host.ProxyJump    ??= value; break;
            case "proxycommand":  host.ProxyCommand ??= value; break;
            // Unknown directives ignored on purpose — v1 scope.
        }
    }

    private static string StripComment(string line)
    {
        // OpenSSH treats '#' as a comment only when preceded by whitespace or at start
        // of line; we apply the simpler rule (any '#' starts a comment) — good enough
        // for the literal hosts we care about.
        var idx = line.IndexOf('#');
        return idx < 0 ? line : line[..idx];
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
        return s;
    }

    private static bool IsWildcardPattern(string p)
        => p.IndexOfAny(new[] { '*', '?', '!' }) >= 0;

    private static bool MatchesAny(string alias, string[] patterns)
    {
        foreach (var raw in patterns)
        {
            bool negate = raw.StartsWith('!');
            var pat = negate ? raw[1..] : raw;
            bool match = MatchGlob(alias, pat);
            if (negate && match) return false;
            if (!negate && match) return true;
        }
        return false;
    }

    private static bool MatchGlob(string text, string pattern)
    {
        // Convert OpenSSH glob (* and ?) to a regex.
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in pattern)
        {
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _   => Regex.Escape(c.ToString()),
            });
        }
        sb.Append('$');
        return Regex.IsMatch(text, sb.ToString(), RegexOptions.IgnoreCase);
    }

    private static IEnumerable<string> ExpandInclude(string value, string parentPath)
    {
        var raw = StripQuotes(value);
        var expanded = ExpandHome(raw);

        // Relative paths are resolved relative to the directory of the including file
        // (OpenSSH's behaviour for user config; system config uses /etc/ssh, but we
        // accept either since the parent path tells us where we came from).
        if (!Path.IsPathRooted(expanded))
        {
            var parentDir = Path.GetDirectoryName(parentPath) ?? string.Empty;
            expanded = Path.Combine(parentDir, expanded);
        }

        var dir = Path.GetDirectoryName(expanded);
        var pattern = Path.GetFileName(expanded);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(pattern))
            yield break;

        IEnumerable<string> matches;
        try { matches = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
        catch { yield break; }

        foreach (var match in matches) yield return match;
    }

    private static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path[0] != '~') return path;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1) return home;
        if (path[1] is '/' or '\\') return Path.Combine(home, path[2..]);
        return path; // ~user form — not supported, pass through.
    }

    private static string? TryGetCanonical(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    // Convert to SessionGroup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a read-only <see cref="SessionGroup"/> from parsed hosts. The group's
    /// <see cref="SessionGroup.SourcePath"/> is set to <paramref name="sourcePath"/>
    /// so the Refresh action can re-read it later.
    /// </summary>
    public static SessionGroup ToSessionGroup(string sourcePath, IReadOnlyList<ParsedSshHost> hosts)
    {
        var group = new SessionGroup
        {
            Name       = $"SSH config — {Path.GetFileName(sourcePath)}",
            IsReadOnly = true,
            OriginKind = "ssh-config",
            SourcePath = sourcePath,
        };

        foreach (var h in hosts)
            group.Sessions.Add(ToDefinition(h));

        return group;
    }

    private static SessionDefinition ToDefinition(ParsedSshHost h)
    {
        var settings = new SshSettings
        {
            Host         = string.IsNullOrWhiteSpace(h.HostName) ? h.Alias : h.HostName!,
            Port         = h.Port ?? 22,
            Username     = h.User ?? string.Empty,
            KeyFilePath  = h.IdentityFile,
            ProxyCommand = h.ProxyCommand,
            JumpHosts    = ParseProxyJump(h.ProxyJump),
        };

        return new SessionDefinition
        {
            Name     = h.Alias,
            Kind     = SessionKind.Ssh,
            Settings = settings,
        };
    }

    private static List<JumpHost> ParseProxyJump(string? proxyJump)
    {
        if (string.IsNullOrWhiteSpace(proxyJump)) return new();
        var hops = new List<JumpHost>();
        foreach (var raw in proxyJump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // user@host:port — all parts optional except host
            string user = string.Empty;
            string hostAndPort = raw;
            var at = raw.IndexOf('@');
            if (at >= 0) { user = raw[..at]; hostAndPort = raw[(at + 1)..]; }

            string host = hostAndPort;
            int port = 22;
            var colon = hostAndPort.IndexOf(':');
            if (colon >= 0)
            {
                host = hostAndPort[..colon];
                int.TryParse(hostAndPort[(colon + 1)..], out port);
                if (port <= 0) port = 22;
            }

            hops.Add(new JumpHost
            {
                Kind     = JumpHostKind.Inline,
                Host     = host,
                Port     = port,
                Username = user,
            });
        }
        return hops;
    }
}
