using System.IO;
using System.Threading.Channels;
using Porta.Pty;

namespace TermThing.Tmux;

/// <summary>
/// One tmux pane presented as an <see cref="IPtyConnection"/>, attached to a
/// <c>TerminalControl</c> with <c>AttachConnection</c>. Output arrives from the
/// control channel's <c>%output</c> lines (<see cref="Feed"/>); it is queued until
/// the terminal reads it, so output that lands before the terminal exists is kept.
/// <para>
/// The pane's size belongs to tmux (the whole client is sized with
/// <c>refresh-client -C</c>), so <see cref="Resize"/> does nothing. <see cref="Kill"/>
/// only ends the stream: a terminal kills its connection when it is closed, and that
/// must never take the tmux pane down with it. Closing a pane is an explicit tmux command.
/// </para>
/// </summary>
public sealed class TmuxPaneConnection : IPtyConnection
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    public TmuxPaneConnection(int paneId, Action<int, byte[]>? input = null)
    {
        PaneId = paneId;
        ReaderStream = new OutputStream(_output.Reader);
        WriterStream = new InputStream(paneId, input);
    }

    /// <summary>The tmux pane id (<c>%N</c> → N).</summary>
    public int PaneId { get; }

    /// <summary>Queues output for the terminal. Thread-safe; called on the control channel's reader thread.</summary>
    public void Feed(byte[] data) => _output.Writer.TryWrite(data);

    /// <summary>Ends the stream: the terminal sees EOF once it has read what is queued.</summary>
    public void Close() => _output.Writer.TryComplete();

    public bool IsClosed => _output.Reader.Completion.IsCompleted;

    public Stream ReaderStream { get; }
    public Stream WriterStream { get; }
    public bool SupportsCancellableRead => true;
    public int Pid => 0;
    public int ExitCode => 0;

#pragma warning disable CS0067 // never raised: EOF on the reader is how the terminal learns the pane is gone
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;
#pragma warning restore CS0067

    public bool WaitForExit(int milliseconds) =>
        _output.Reader.Completion.Wait(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));

    public void Kill() => Close();
    public void Resize(int cols, int rows) { }
    public void Dispose() => Close();

    /// <summary>Reads the queued chunks; returns 0 once the pane is closed and drained.</summary>
    private sealed class OutputStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[]? _current;
        private int _offset;

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (_current is null || _offset >= _current.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (reader.TryRead(out var next)) { _current = next; _offset = 0; }
            }
            int n = Math.Min(count, _current.Length - _offset);
            Buffer.BlockCopy(_current, _offset, buffer, offset, n);
            _offset += n;
            // Hand the terminal everything already queued in one read: fewer, larger parses.
            while (n < count && reader.TryPeek(out var more) && more.Length <= count - n && reader.TryRead(out more))
            {
                Buffer.BlockCopy(more, 0, buffer, offset + n, more.Length);
                n += more.Length;
            }
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Bytes the terminal sends (keys, replies); handed to <c>input</c>, or dropped when there is none.</summary>
    private sealed class InputStream(int paneId, Action<int, byte[]>? input) : Stream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (input is null || count == 0) return;
            input(paneId, buffer.AsSpan(offset, count).ToArray());
        }

        // The terminal writes asynchronously; handing over is cheap, so do it inline
        // rather than through Stream's default thread-pool hop.
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (input is not null && !buffer.IsEmpty) input(paneId, buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
