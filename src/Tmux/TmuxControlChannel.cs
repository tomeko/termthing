using System.Diagnostics;
using System.IO;
using System.Text;
using Renci.SshNet;
using TermThing.Ssh;

namespace TermThing.Tmux;

/// <summary>
/// A tmux control-mode client (<c>tmux -C</c>) running on its own SSH exec channel,
/// next to (and independent of) any interactive shell. tmux reads our commands from
/// the channel's stdin and writes replies and notifications to its stdout.
/// <para>
/// No PTY is involved: plain <c>-C</c> works over a pipe, so there is no DCS wrapping
/// (that is <c>-CC</c>) and the OSC-7 shell-integration injector is never in the path.
/// </para>
/// <para>
/// Events are raised on the reader thread. <see cref="Output"/> is hot (every byte any
/// pane prints), so handlers must be cheap and thread-safe.
/// </para>
/// </summary>
public sealed class TmuxControlChannel : IDisposable
{
    private readonly SshClient _client;
    private readonly string _command;
    private readonly object _writeLock = new();
    private readonly Queue<TaskCompletionSource<TmuxReply>> _pending = new();
    private SshCommand? _cmd;
    private Stream? _input;
    private Thread? _reader;
    private volatile bool _disposed;
    private int _closedFired;
    private string? _exitReason;

    /// <param name="client">A connected client; the channel never disconnects or disposes it.</param>
    /// <param name="tmuxArgs">Everything after <c>tmux -C</c>, already quoted for <c>sh</c>.</param>
    public TmuxControlChannel(SshClient client, string tmuxArgs)
    {
        _client = client;
        _command = "exec tmux -C " + tmuxArgs;
    }

    /// <summary>Attaches to <paramref name="session"/>, creating it if it doesn't exist.</summary>
    public static TmuxControlChannel AttachOrCreate(SshClient client, string session) =>
        new(client, "new-session -A -s " + TmuxClient.Quote(session));

    /// <summary>Raw output of a pane (<c>%output</c>), decoded. Raised on the reader thread.</summary>
    public event Action<int, byte[]>? Output;

    /// <summary>Any other notification. Raised on the reader thread.</summary>
    public event Action<TmuxNotification>? Notification;

    /// <summary>
    /// Raised once when the control client is gone: tmux exited (<c>%exit</c>, with its
    /// reason if any), the channel failed, or tmux could not start (stderr is the reason).
    /// </summary>
    public event Action<string?>? Closed;

    public bool IsClosed => _closedFired != 0;

    /// <summary>Why the channel closed (as passed to <see cref="Closed"/>), once it has.</summary>
    public string? CloseReason { get; private set; }

    /// <summary>Starts the remote <c>tmux -C</c> and the reader thread. Blocks briefly; call off the UI thread.</summary>
    public void Start()
    {
        _cmd = TmuxClient.CreateCommand(_client, _command);
        _ = _cmd.ExecuteAsync(CancellationToken.None).ContinueWith(_ => { }, TaskScheduler.Default);
        _input = _cmd.CreateInputStream();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "tmux -C reader" };
        _reader.Start();
    }

    /// <summary>
    /// Sends one tmux command line and returns its reply. tmux answers commands in the
    /// order they arrive, so replies are matched to a FIFO queue.
    /// </summary>
    public Task<TmuxReply> SendAsync(string command)
    {
        var tcs = new TaskCompletionSource<TmuxReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed || IsClosed || _input is null)
        {
            tcs.SetException(new InvalidOperationException("tmux control channel is closed."));
            return tcs.Task;
        }
        var bytes = Encoding.UTF8.GetBytes(command.Replace('\n', ' ') + "\n");
        try
        {
            lock (_writeLock)
            {
                _pending.Enqueue(tcs);
                _input.Write(bytes, 0, bytes.Length);
                _input.Flush();
            }
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return tcs.Task;
    }

    /// <summary>Sends a command whose reply nobody needs; failures are only logged.</summary>
    public void Post(string command) =>
        SendAsync(command).ContinueWith(t =>
        {
            if (t.IsFaulted) Debug.WriteLine($"[tmux] '{command}' failed: {t.Exception?.GetBaseException().Message}");
            else if (!t.Result.Success) Debug.WriteLine($"[tmux] '{command}': {t.Result.Text}");
        }, TaskScheduler.Default);

    // -----------------------------------------------------------------------
    // Reader
    // -----------------------------------------------------------------------

    private void ReadLoop()
    {
        string? error = null;
        try
        {
            var stream = _cmd!.OutputStream;
            var buffer = new byte[64 * 1024];
            var line = new MemoryStream();
            List<string>? block = null;   // lines of the reply being collected
            bool blockIsOurs = false;

            while (!_disposed)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                int start = 0;
                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    HandleLine(line.GetBuffer().AsSpan(0, (int)line.Length), ref block, ref blockIsOurs);
                    line.SetLength(0);
                }
                line.Write(buffer, start, read - start);
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            error = ex.Message;
        }
        catch { }

        if (_exitReason is null && error is null && !_disposed)
        {
            // tmux failed before entering control mode (e.g. "command not found"): its
            // complaint is on stderr. Bounded, in case the stream never completes.
            try
            {
                var stderrTask = Task.Run(() =>
                {
                    using var sr = new StreamReader(_cmd!.ExtendedOutputStream, Encoding.UTF8);
                    return sr.ReadToEnd().Trim();
                });
                if (stderrTask.Wait(TimeSpan.FromSeconds(1)) && stderrTask.Result.Length > 0)
                    error = stderrTask.Result;
            }
            catch { }
        }
        FireClosed(_exitReason ?? error);
    }

    private void HandleLine(ReadOnlySpan<byte> raw, ref List<string>? block, ref bool blockIsOurs)
    {
        if (raw.Length > 0 && raw[^1] == (byte)'\r') raw = raw[..^1];

        // Hot path first: pane output is the bulk of the traffic, and is never inside a block.
        if (block is null && TmuxControlProtocol.TryParseOutput(raw, out var paneId, out var data))
        {
            try { Output?.Invoke(paneId, data); }
            catch (Exception ex) { Debug.WriteLine($"[tmux] output handler failed: {ex}"); }
            return;
        }

        var text = Encoding.UTF8.GetString(raw);
        if (TmuxControlProtocol.TryParseGuard(text, out var kind, out _, out var ours))
        {
            if (kind == "begin" && block is null)
            {
                block = new List<string>();
                blockIsOurs = ours;
                return;
            }
            if (kind is "end" or "error" && block is not null)
            {
                if (blockIsOurs) CompleteNext(new TmuxReply(kind == "end", block));
                block = null;
                return;
            }
        }

        if (block is not null)
        {
            block.Add(text);
            return;
        }

        if (TmuxControlProtocol.ParseNotification(text) is { } n)
        {
            if (n.Name == "exit") _exitReason = n.Rest.Length > 0 ? n.Rest : "the session was closed or this client was detached";
            try { Notification?.Invoke(n); }
            catch (Exception ex) { Debug.WriteLine($"[tmux] notification handler failed: {ex}"); }
        }
    }

    private void CompleteNext(TmuxReply reply)
    {
        TaskCompletionSource<TmuxReply>? tcs;
        lock (_writeLock) _pending.TryDequeue(out tcs);
        tcs?.TrySetResult(reply);
    }

    private void FireClosed(string? reason)
    {
        CloseReason ??= reason;
        if (Interlocked.Exchange(ref _closedFired, 1) != 0) return;
        lock (_writeLock)
        {
            while (_pending.TryDequeue(out var tcs))
                tcs.TrySetException(new InvalidOperationException("tmux control channel closed."));
        }
        Closed?.Invoke(reason);
    }

    /// <summary>
    /// Ends the control client. Closing its stdin makes tmux detach this client; the
    /// session and its panes keep running on the host.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _input?.Dispose(); } catch { }
        try { _cmd?.Dispose(); } catch { }
        FireClosed(null);
    }
}
