using Renci.SshNet;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace TermThing.Ssh;

/// <summary>Lifecycle state reported by an <see cref="ILogSource"/>.</summary>
public enum LogStreamState
{
    /// <summary>Lines are flowing (initial attach, or reattached after a restart).</summary>
    Streaming,
    /// <summary>The thing being followed stopped (container exited/removed). May resume.</summary>
    Stopped,
    /// <summary>The stream is over for good (command exited, SSH dropped).</summary>
    Ended,
}

public sealed record LogStreamStatus(LogStreamState State, string Message);

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

    /// <summary>
    /// Raised when the stream's state changes. The last status raised is final
    /// (<see cref="LogStreamState.Ended"/>, or <see cref="LogStreamState.Stopped"/> for a
    /// removed container). Fires on a background thread; never after Dispose.
    /// </summary>
    event EventHandler<LogStreamStatus>? StatusChanged;

    /// <summary>Begins streaming. Safe to call once.</summary>
    void Start();
}

/// <summary>
/// Shared base implementation: runs commands over SSH exec channels on a
/// background task. <see cref="Stream"/> pipes a command's stdout line-by-line
/// to <see cref="ILogSource.LineReceived"/>.
/// </summary>
public abstract class SshExecLogSource : ILogSource
{
    private readonly object _gate = new();
    private SshCommand? _current;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    protected SshClient Client { get; }
    public string Label { get; }
    public event EventHandler<string>? LineReceived;
    public event EventHandler<LogStreamStatus>? StatusChanged;

    protected SshExecLogSource(SshClient client, string label)
    {
        Client = client;
        Label = label;
    }

    public void Start()
    {
        CancellationToken ct;
        lock (_gate)
        {
            if (_cts != null || _disposed) return;
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
        }
        Task.Run(() =>
        {
            LogStreamStatus final;
            try { final = Run(ct); }
            catch (Exception ex) { final = new(LogStreamState.Ended, $"(stream ended: {ex.Message})"); }
            if (!ct.IsCancellationRequested) RaiseStatus(final);
        });
    }

    /// <summary>Body of the background task. Returns the final status.</summary>
    protected abstract LogStreamStatus Run(CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="command"/> and raises a line event per stdout line until
    /// it exits or <paramref name="ct"/> fires. Returns null on clean exit, else the error.
    /// </summary>
    protected string? Stream(string command, CancellationToken ct)
    {
        SshCommand cmd;
        lock (_gate)
        {
            if (_disposed) return null;
            cmd = _current = Client.CreateCommand(command);
        }
        cmd.CommandTimeout = TimeSpan.FromHours(24); // long-running
        try
        {
            var asyncResult = cmd.BeginExecute();
            using var reader = new StreamReader(cmd.OutputStream);
            while (!ct.IsCancellationRequested)
            {
                var line = reader.ReadLine();
                if (line == null) break; // EOF
                RaiseLine(line);
            }
            if (!asyncResult.IsCompleted && !ct.IsCancellationRequested)
                cmd.EndExecute(asyncResult);
            return null;
        }
        catch (Exception ex)
        {
            return ct.IsCancellationRequested ? null : ex.Message;
        }
        finally
        {
            lock (_gate) { if (_current == cmd) _current = null; }
            try { cmd.Dispose(); } catch { }
        }
    }

    /// <summary>Runs a short command; returns (exit status, stdout). Throws on transport errors.</summary>
    protected (int Exit, string Output) Execute(string command, TimeSpan timeout)
    {
        using var cmd = Client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        var output = cmd.Execute();
        return (cmd.ExitStatus ?? -1, output);
    }

    protected void RaiseLine(string line) => LineReceived?.Invoke(this, line);

    protected void RaiseStatus(LogStreamStatus status)
    {
        if (_disposed) return;
        try { StatusChanged?.Invoke(this, status); } catch { }
    }

    public void Dispose()
    {
        SshCommand? cmd;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            cmd = _current;
        }
        try { _cts?.Cancel(); } catch { }
        try { cmd?.CancelAsync(); } catch { }
    }
}

/// <summary>
/// Tails a remote file via <c>tail -n 200 -F -- &lt;path&gt;</c>.
/// </summary>
public sealed class SshTailLogSource : SshExecLogSource
{
    private readonly string _command;

    public SshTailLogSource(SshClient client, string remotePath)
        : base(client, remotePath)
    {
        _command = $"tail -n 200 -F -- '{remotePath.Replace("'", "'\\''")}'";
    }

    protected override LogStreamStatus Run(CancellationToken ct)
    {
        var error = Stream(_command, ct);
        return new(LogStreamState.Ended, error == null ? "(stream ended)" : $"(stream ended: {error})");
    }
}

/// <summary>
/// Streams <c>docker logs -f</c> for one container and follows its lifecycle:
/// when the stream ends it inspects the container to tell a stop from a removal
/// or an SSH drop, then polls for a restart and reattaches with
/// <c>--since &lt;FinishedAt&gt;</c> so earlier lines aren't repeated.
/// </summary>
public sealed class DockerLogsSource : SshExecLogSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly string _id;

    public DockerLogsSource(SshClient client, string containerId, string label)
        : base(client, label)
    {
        _id = QuoteId(containerId);
    }

    protected override LogStreamStatus Run(CancellationToken ct)
    {
        // StartedAt of the run we're attached to; a different value later means a restart.
        var attachedRun = Inspect()?.StartedAt;
        string? since = null;

        while (!ct.IsCancellationRequested)
        {
            // Container stderr arrives on docker's stderr, so merge it in.
            var cmd = since == null
                ? $"docker logs -f --tail 200 {_id} 2>&1"
                : $"docker logs -f --since '{since}' {_id} 2>&1";
            var error = Stream(cmd, ct);
            if (ct.IsCancellationRequested) break;
            if (error != null || !Client.IsConnected)
                return new(LogStreamState.Ended, $"(stream ended: {error ?? "SSH connection closed"})");

            // Stream ended: find out why, then wait for the container to come back.
            bool stopReported = false;
            while (!ct.IsCancellationRequested)
            {
                ContainerState? state;
                try { state = Inspect(); }
                catch (Exception ex) { return new(LogStreamState.Ended, $"(stream ended: {ex.Message})"); }

                if (state == null)
                {
                    RaiseLine("──── container removed ────");
                    return new(LogStreamState.Stopped, "Container removed");
                }

                if (state.Status == "running" && state.StartedAt != attachedRun)
                {
                    // Restarted (possibly before we got to see it stopped).
                    if (!stopReported) RaiseLine($"──── container stopped: {state.Describe()} ────");
                    RaiseLine($"──── container started at {FormatTime(state.StartedAt)} ────");
                    attachedRun = state.StartedAt;
                    since = state.HasFinished ? state.FinishedAt : state.StartedAt;
                    RaiseStatus(new(LogStreamState.Streaming, ""));
                    break;
                }

                if (!stopReported)
                {
                    stopReported = true;
                    if (state.Status == "running")
                    {
                        // Same run still alive, but docker logs exited (e.g. daemon hiccup).
                        RaiseStatus(new(LogStreamState.Stopped, "Log stream ended — container still running"));
                    }
                    else
                    {
                        RaiseLine($"──── container stopped: {state.Describe()} ────");
                        RaiseStatus(new(LogStreamState.Stopped, $"Container stopped — {state.Describe()} · waiting for restart"));
                    }
                }

                if (ct.WaitHandle.WaitOne(PollInterval)) break;
            }
        }
        return new(LogStreamState.Ended, "(stream ended)");
    }

    /// <summary>Returns the container's state, or null if it no longer exists.</summary>
    private ContainerState? Inspect()
    {
        var (exit, output) = Execute(
            $"docker inspect -f '{{{{.State.Status}}}}|{{{{.State.ExitCode}}}}|{{{{.State.OOMKilled}}}}|{{{{.State.StartedAt}}}}|{{{{.State.FinishedAt}}}}' {_id}",
            TimeSpan.FromSeconds(8));
        if (exit != 0) return null;
        var p = output.Trim().Split('|');
        if (p.Length < 5) return null;
        return new ContainerState(
            p[0],
            int.TryParse(p[1], out var code) ? code : 0,
            p[2] == "true",
            p[3],
            p[4]);
    }

    private sealed record ContainerState(string Status, int ExitCode, bool OomKilled, string StartedAt, string FinishedAt)
    {
        /// <summary>Docker reports 0001-01-01T00:00:00Z for a container that never finished.</summary>
        public bool HasFinished => !FinishedAt.StartsWith("0001-", StringComparison.Ordinal);

        public string Describe()
        {
            var reason = OomKilled ? " (OOM-killed)" : ExitCode > 128 ? $" ({SignalName(ExitCode - 128)})" : "";
            var when = HasFinished ? $" at {FormatTime(FinishedAt)}" : "";
            return $"exit {ExitCode}{reason}{when}";
        }
    }

    private static string SignalName(int sig) => sig switch
    {
        2  => "SIGINT",
        6  => "SIGABRT",
        9  => "SIGKILL",
        11 => "SIGSEGV",
        15 => "SIGTERM",
        _  => $"signal {sig}",
    };

    /// <summary>Docker timestamp (RFC 3339, nanoseconds, UTC) → local time for display.</summary>
    private static string FormatTime(string ts)
    {
        // .NET parses at most 7 fractional digits; docker emits up to 9.
        var trimmed = Regex.Replace(ts, @"(\.\d{7})\d+", "$1");
        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return ts;
        var local = t.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm:ss") : local.ToString("yyyy-MM-dd HH:mm:ss");
    }

    // Container IDs are hex; restrict to a safe charset so we can pass through unquoted.
    private static string QuoteId(string id)
    {
        foreach (var c in id)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return "'" + id.Replace("'", "'\\''") + "'";
        return id;
    }
}
