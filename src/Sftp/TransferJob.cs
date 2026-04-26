namespace TermThing.Sftp;

public sealed class TransferProgressEventArgs : EventArgs
{
    public string FileName        { get; init; } = string.Empty;
    public long   TransferredBytes { get; init; }
    public long   TotalBytes       { get; init; }
    public bool   IsUpload         { get; init; }
}

/// <summary>
/// Represents a single file upload or download queued for transfer.
/// </summary>
public sealed class TransferJob
{
    public bool   IsUpload    { get; init; }
    public string LocalPath   { get; init; } = string.Empty;
    public string RemotePath  { get; init; } = string.Empty;
    public long   TotalBytes  { get; init; }
}
