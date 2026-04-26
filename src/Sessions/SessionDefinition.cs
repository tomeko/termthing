using System.Text.Json.Serialization;

namespace TermThing.Sessions;

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
