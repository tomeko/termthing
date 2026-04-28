using System.Collections.Generic;
using System.Text.Json.Serialization;

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

    // Secrets are never persisted — always prompted at connect time.
    [JsonIgnore] public string? TransientPassword { get; set; }
    [JsonIgnore] public string? TransientKeyPassphrase { get; set; }
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

    /// <summary>Returns a deep copy with a fresh <see cref="Id"/>.</summary>
    public SessionDefinition Duplicate()
    {
        return new SessionDefinition
        {
            Id = Guid.NewGuid(),
            Name = Name + " (copy)",
            Kind = Kind,
            GroupId = GroupId,
            Settings = Settings,
        };
    }
}
