using Renci.SshNet;
using System.IO;
using System.Threading;

namespace TermThing.Ssh;

/// <summary>
/// Background log line stream over an SSH exec channel. Implementations are
/// backed by <c>tail -F &lt;path&gt;</c> or <c>docker logs -f &lt;id&gt;</c>.
/// Lines are raised on a background thread; the consumer marshals to UI.
/// </summary>
public interface ILogSource : IDisposable
{
    /// <summary>Human-readable label for the source (used in window title / path box).</summary>
    string Label { get; }

    /// <summary>Raised once per logical line (no trailing newline). May fire on any thread.</summary>
    event EventHandler<string>? LineReceived;

    /// <summary>Raised once when the underlying command exits. Fires on a background thread.</summary>
    event EventHandler<string?>? Closed;

    /// <summary>Begins streaming. Safe to call once.</summary>
    void Start();
}

/// <summary>
/// Shared base implementation: runs a single-line command via an SSH exec channel
/// and streams stdout line-by-line to <see cref="ILogSource.LineReceived"/>.
/// </summary>
public abstract class SshExecLogSource : ILogSource
{
    private readonly SshClient _client;
    private readonly string _command;
    private SshCommand? _cmd;
    private CancellationTokenSource? _cts;
    private Task? _readerTask;
    private bool _disposed;

    public string Label { get; }
    public event EventHandler<string>? LineReceived;
    public event EventHandler<string?>? Closed;

    protected SshExecLogSource(SshClient client, string label, string command)
    {
        _client = client;
        Label = label;
        _command = command;
    }

    public void Start()
    {
        if (_cmd != null || _disposed) return;
        _cts = new CancellationTokenSource();
        _cmd = _client.CreateCommand(_command);
        _cmd.CommandTimeout = TimeSpan.FromHours(24); // long-running

        // Kick off async exec; we'll read OutputStream concurrently.
        var asyncResult = _cmd.BeginExecute();

        _readerTask = Task.Run(() => ReadLoop(_cmd, asyncResult, _cts.Token));
    }

    private void ReadLoop(SshCommand cmd, IAsyncResult asyncResult, CancellationToken ct)
    {
        string? error = null;
        try
        {
            using var reader = new StreamReader(cmd.OutputStream);
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break; // EOF
                LineReceived?.Invoke(this, line);
            }

            if (!asyncResult.IsCompleted)
                cmd.EndExecute(asyncResult);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            try { Closed?.Invoke(this, error); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _cmd?.CancelAsync(); } catch { }
        try { _cmd?.Dispose(); } catch { }
    }
}

/// <summary>
/// Tails a remote file via <c>tail -n 200 -F -- &lt;path&gt;</c>.
/// </summary>
public sealed class SshTailLogSource : SshExecLogSource
{
    public SshTailLogSource(SshClient client, string remotePath)
        : base(client, remotePath,
            $"tail -n 200 -F -- '{remotePath.Replace("'", "'\\''")}'")
    { }
}

/// <summary>
/// Streams <c>docker logs -f --tail 200 &lt;id&gt;</c>. Reserved for future
/// DockerMon "Logs" button integration.
/// </summary>
public sealed class DockerLogsSource : SshExecLogSource
{
    public DockerLogsSource(SshClient client, string containerId, string label)
        : base(client, label, $"docker logs -f --tail 200 {containerId}")
    { }
}
