using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TermThing.Sessions;

// ---------------------------------------------------------------------------
// Jump-host support
// ---------------------------------------------------------------------------

/// <summary>Whether a jump hop is defined by reference to a saved session or inline.</summary>
public enum JumpHostKind { SessionRef, Inline }

/// <summary>
/// One hop in a jump-host chain. Either references an existing saved SSH session
/// by <see cref="SessionId"/>, or carries its own inline connection details.
/// Secrets are never persisted — they are prompted at connect time if needed.
/// </summary>
public sealed class JumpHost
{
    public JumpHostKind Kind { get; set; } = JumpHostKind.Inline;

    // --- SessionRef fields ---
    /// <summary>Id of the saved SSH <see cref="SessionDefinition"/> to use as this hop.</summary>
    public Guid? SessionId { get; set; }

    // --- Inline fields ---
    public string Host     { get; set; } = string.Empty;
    public int    Port     { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string? KeyFilePath { get; set; }

    // Secrets — never persisted.
    [JsonIgnore] public string? TransientPassword       { get; set; }
    [JsonIgnore] public string? TransientKeyPassphrase  { get; set; }
}

// ---------------------------------------------------------------------------
// Bookmark — a pinned absolute remote folder path stored per SSH session
// ---------------------------------------------------------------------------

/// <summary>
/// A pinned remote folder path shown in the SFTP bookmarks panel.
/// <see cref="Name"/> defaults to <see cref="AbsolutePath"/> but can be
/// customised by the user via the rename context-menu action.
/// </summary>
public sealed record Bookmark
{
    public string Name { get; init; } = string.Empty;
    public string AbsolutePath { get; init; } = string.Empty;
}

// ---------------------------------------------------------------------------
// Per-kind settings (polymorphic; serialised as part of SessionDefinition)
// ---------------------------------------------------------------------------

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LocalSettings), "local")]
[JsonDerivedType(typeof(SshSettings), "ssh")]
[JsonDerivedType(typeof(SerialSettings), "serial")]
public abstract record SessionSettings;

public record LocalSettings : SessionSettings
{
    public string Process { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
}

public record SshSettings : SessionSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public string? KeyFilePath { get; init; }
    public bool EnableSftp { get; init; }
    public bool ShellIntegrationOsc7 { get; init; } = true;
    public string Term { get; init; } = "xterm-256color";

    /// <summary>
    /// Ordered list of jump hops through which this session is routed.
    /// Empty (default) means a direct connection.
    /// </summary>
    public List<JumpHost> JumpHosts { get; init; } = [];

    /// <summary>Pinned remote folder paths shown in the SFTP bookmarks panel.</summary>
    public List<Bookmark> Bookmarks { get; init; } = [];

    /// <summary>Whether the Sysmon stats strip is shown for this session.</summary>
    public bool SysmonEnabled { get; init; }

    /// <summary>Whether the DockerMon container panel is shown for this session.</summary>
    public bool DockerMonEnabled { get; init; }

    /// <summary>Reserved for future per-session DockerMon panel height (px).</summary>
    public double DockerMonHeightPx { get; init; }

    // Secrets are never persisted — always prompted at connect time.
    [JsonIgnore] public string? TransientPassword { get; set; }
    [JsonIgnore] public string? TransientKeyPassphrase { get; set; }

    /// <summary>
    /// Set to true when the user has just been shown the SSH credentials dialog
    /// (e.g. via File ▸ New SSH) so the launcher does not re-prompt with an
    /// identical-looking dialog. Not persisted.
    /// </summary>
    [JsonIgnore] public bool TransientSecretsConfirmed { get; set; }
}

public record SerialSettings : SessionSettings
{
    public string PortName { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public string Parity { get; init; } = "None";
    public string StopBits { get; init; } = "One";
    public string FlowControl { get; init; } = "None";
}

// ---------------------------------------------------------------------------
// Session definition — one row in the sessions tree
// ---------------------------------------------------------------------------

public sealed class SessionDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "New Session";
    public SessionKind Kind { get; init; }
    public Guid? GroupId { get; set; }
    public SessionSettings? Settings { get; set; }

    /// <summary>
    /// <see cref="Material.Icons.MaterialIconKind"/> enum member name
    /// (e.g. <c>"Earth"</c>). <c>null</c> means use the kind default.
    /// </summary>
    public string? IconKind { get; set; }

    /// <summary>
    /// Hex colour string (e.g. <c>"#6FA8DC"</c>) to tint the session icon.
    /// <c>null</c> means use the theme default (white/light grey).
    /// </summary>
    public string? IconColor { get; set; }

    /// <summary>
    /// Returns a shallow clone keeping the same name and settings reference.
    /// Used for Cut/Copy so that the name is not modified until Paste.
    /// </summary>
    public SessionDefinition Clone() => new()
    {
        Id       = Guid.NewGuid(),
        Name     = Name,
        Kind     = Kind,
        GroupId  = GroupId,
        Settings = Settings,
        IconKind  = IconKind,
        IconColor = IconColor,
    };

    /// <summary>
    /// Returns a copy with a unique name suffix. If the name already ends in
    /// <c>" (copy)"</c> or <c>" (copy N)"</c> the counter is incremented instead
    /// of appending another <c>" (copy)"</c>.
    /// </summary>
    public SessionDefinition Duplicate()
    {
        var baseName = DeriveCopyBaseName(Name, out int next);
        var newName  = next == 1 ? $"{baseName} (copy)" : $"{baseName} (copy {next})";
        return new SessionDefinition
        {
            Id       = Guid.NewGuid(),
            Name     = newName,
            Kind     = Kind,
            GroupId  = GroupId,
            Settings = Settings,
            IconKind  = IconKind,
            IconColor = IconColor,
        };
    }

    // Strips any existing " (copy)" / " (copy N)" suffix and returns the next counter value.
    private static readonly Regex CopySuffixRegex =
        new(@"^(.*?) \(copy(?: (\d+))?\)$", RegexOptions.RightToLeft | RegexOptions.Compiled);

    private static string DeriveCopyBaseName(string name, out int nextCounter)
    {
        var m = CopySuffixRegex.Match(name);
        if (!m.Success) { nextCounter = 1; return name; }
        nextCounter = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) + 1 : 2;
        return m.Groups[1].Value;
    }
}
