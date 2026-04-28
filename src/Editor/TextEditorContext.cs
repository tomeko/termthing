namespace TermThing.Editor;

/// <summary>
/// All the data a <see cref="TermThing.Views.TextEditorWindow"/> needs to display
/// and upload a remote file.  The <see cref="UploadAsync"/> delegate closes over
/// the <c>SftpClient</c> so the window never holds a direct reference to the
/// SSH connection.
/// </summary>
public sealed class TextEditorContext
{
    /// <summary>Stable ID that identifies the running SFTP session (one per tab).</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Full remote path, e.g. <c>/home/user/.bashrc</c>.</summary>
    public required string RemotePath { get; init; }

    /// <summary>Local temp file path where the download was written.</summary>
    public required string LocalTempPath { get; init; }

    /// <summary>Display label shown in the window title, e.g. <c>user@host</c>.</summary>
    public required string DisplayHost { get; init; }

    /// <summary>
    /// Uploads <paramref name="bytes"/> to the remote path, overwriting the source
    /// file.  May throw if the underlying SFTP connection has been closed.
    /// </summary>
    public required Func<byte[], CancellationToken, Task> UploadAsync { get; init; }
}
