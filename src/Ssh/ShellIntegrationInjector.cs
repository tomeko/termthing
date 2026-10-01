using System.IO;
using System.Text;

namespace TermThing.Ssh;

/// <summary>
/// Injects a one-shot shell-integration command into an SSH shell and hides the
/// shell's echo of it, working on the raw byte stream between the SSH channel and
/// the terminal.
/// <para>
/// The command must carry <see cref="Sentinel"/> (on a variable assignment — zsh does
/// not enable interactive comments by default); any output line containing it is
/// dropped. Matching on bytes is safe because the sentinel and <c>\n</c> are ASCII,
/// which never occur inside a multi-byte UTF-8 sequence.
/// </para>
/// <para>
/// It also types plain lines for the user once the shell is idle
/// (<see cref="SendWhenIdle"/>), e.g. reattaching tmux after a reconnect. Those go out
/// in the same write as the integration command, after it, so the integration command
/// always reaches the login shell rather than whatever the queued line starts.
/// </para>
/// </summary>
internal sealed class ShellIntegrationInjector
{
    public const string Sentinel = "__ICTERMINT__";
    private static readonly byte[] SentinelBytes = Encoding.ASCII.GetBytes(Sentinel);

    // Writing on the first byte races the remote shell's startup: the bytes land in
    // the tty input buffer before the line editor is reading, so the command is
    // neither executed nor echoed until the user next presses a key — by which time
    // the echo hold is long gone. Waiting for a lull means the prompt is drawn.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(750);

    // Bounds on the hold, so output can never be swallowed permanently if the echo
    // does not come back in the shape we expect.
    private const int HoldMaxBytes = 8192;
    private static readonly TimeSpan HoldTimeout = TimeSpan.FromSeconds(2);

    private readonly Stream _writer;
    private readonly Action<byte[]> _release;
    private readonly object _lock = new();

    private string? _command;
    private bool _commandSent;
    private readonly List<string> _queued = new();
    private bool _pumpRunning;
    private bool _sawOutput;
    private CancellationToken _ct;
    private long _lastOutputTicks;

    // Echo hold. A line editor (zsh's ZLE especially) redraws the line it echoes in
    // several writes, so the sentinel can straddle reads. Once injected, the trailing
    // partial line is held back and only released after a newline, so matching always
    // sees whole logical lines.
    private readonly List<byte> _hold = new();
    private bool _filtering;

    /// <param name="writer">The shell's input stream, to write the command into.</param>
    /// <param name="release">
    /// Delivers held output when the hold times out. The reader may be idle at that
    /// point, so it must be woken to pick the bytes up.
    /// </param>
    public ShellIntegrationInjector(Stream writer, Action<byte[]> release)
    {
        _writer = writer;
        _release = release;
    }

    /// <summary>
    /// Arms the injection. The command is sent once the shell has produced output and
    /// then gone quiet. Ignored once a command has been armed.
    /// </summary>
    public void Arm(string command)
    {
        lock (_lock)
            _command ??= command;
    }

    /// <summary>
    /// Types <paramref name="line"/> (after Ctrl+U, which clears any partial input) once
    /// the shell has produced output and gone quiet, after the integration command if
    /// one is still pending. Unlike the integration command, its echo is not hidden.
    /// </summary>
    public void SendWhenIdle(string line)
    {
        lock (_lock)
        {
            _queued.Add(line);
            if (_sawOutput) StartPump();
        }
    }

    private bool HasWork => (_command is not null && !_commandSent) || _queued.Count > 0;

    /// <summary>
    /// Passes one chunk of shell output through. Returns <c>null</c> when the chunk is
    /// to be delivered unchanged, otherwise the bytes to deliver now (possibly empty).
    /// </summary>
    public byte[]? Process(ReadOnlySpan<byte> chunk, CancellationToken ct)
    {
        Volatile.Write(ref _lastOutputTicks, DateTime.UtcNow.Ticks);

        lock (_lock)
        {
            _sawOutput = true;
            _ct = ct;
            StartPump();

            if (!_filtering)
            {
                // Late echoes (a shell that re-prints its line after the hold expired)
                // still get the cheap per-chunk treatment.
                return chunk.IndexOf(SentinelBytes) < 0 ? null : StripSentinelLines(chunk);
            }

            return FilterHeld(chunk);
        }
    }

    private byte[] FilterHeld(ReadOnlySpan<byte> chunk)
    {
        _hold.AddRange(chunk);
        var pending = _hold.ToArray();
        _hold.Clear();

        bool overBudget = pending.Length > HoldMaxBytes;
        int lastNewline = Array.LastIndexOf(pending, (byte)'\n');

        if (lastNewline < 0)
        {
            // No complete line yet. Keep waiting unless we have held too much.
            if (!overBudget)
            {
                _hold.AddRange(pending);
                return [];
            }
            _filtering = false;
            return pending;
        }

        var complete = pending.AsSpan(0, lastNewline + 1);
        var tail = pending.AsSpan(lastNewline + 1);

        if (complete.IndexOf(SentinelBytes) >= 0)
        {
            // The echo has been dealt with; stop interfering with the stream.
            _filtering = false;
            return [.. StripSentinelLines(complete), .. tail];
        }

        if (overBudget)
        {
            _filtering = false;
            return pending;
        }

        _hold.AddRange(tail);
        return complete.ToArray();
    }

    /// <summary>Starts the idle-wait-then-write task if there is work and none is running. Caller holds the lock.</summary>
    private void StartPump()
    {
        if (_pumpRunning || !HasWork) return;
        _pumpRunning = true;
        var ct = _ct;
        _ = Task.Run(async () =>
        {
            try
            {
                // Sleep until the stream has been idle for the full quiet period,
                // extending each time more output lands (a slow motd, say).
                while (true)
                {
                    var idle = DateTime.UtcNow - new DateTime(Volatile.Read(ref _lastOutputTicks), DateTimeKind.Utc);
                    if (idle >= QuietPeriod)
                        break;
                    await Task.Delay(QuietPeriod - idle, ct).ConfigureAwait(false);
                }

                var text = new StringBuilder();
                bool hiding = false;
                lock (_lock)
                {
                    if (_command is not null && !_commandSent)
                    {
                        _commandSent = true;
                        hiding = _filtering = true;
                        _hold.Clear();
                        text.Append(_command).Append('\n');
                    }
                    foreach (var line in _queued)
                        text.Append('\x15').Append(line).Append('\n');
                    _queued.Clear();
                }
                if (hiding)
                    _ = Task.Delay(HoldTimeout, CancellationToken.None).ContinueWith(_ => EndHold(), TaskScheduler.Default);

                var bytes = Encoding.UTF8.GetBytes(text.ToString());
                await _writer.WriteAsync(bytes, ct).ConfigureAwait(false);
                await _writer.FlushAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Cancelled or the write failed, so no echo is coming — release now
                // rather than making the user wait out the hold timeout.
                EndHold();
            }
            finally
            {
                // Work queued while we were writing is picked up on the next output
                // chunk, or right away if the shell has already spoken.
                lock (_lock)
                {
                    _pumpRunning = false;
                    if (_queued.Count > 0) StartPump();
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>Stops holding output and delivers whatever was still held. Safe to call more than once.</summary>
    private void EndHold()
    {
        byte[] pending;
        lock (_lock)
        {
            if (!_filtering)
                return;
            _filtering = false;
            pending = _hold.ToArray();
            _hold.Clear();
        }
        if (pending.Length > 0)
            _release(pending);
    }

    /// <summary>Removes every line containing the sentinel, keeping the rest byte-for-byte.</summary>
    private static byte[] StripSentinelLines(ReadOnlySpan<byte> text)
    {
        var result = new List<byte>(text.Length);
        bool first = true;
        while (true)
        {
            int nl = text.IndexOf((byte)'\n');
            var line = nl < 0 ? text : text[..nl];
            if (line.IndexOf(SentinelBytes) < 0)
            {
                if (!first) result.Add((byte)'\n');
                result.AddRange(line);
                first = false;
            }
            if (nl < 0) break;
            text = text[(nl + 1)..];
        }
        return result.ToArray();
    }
}
