using Porta.Pty;
using Renci.SshNet;
using System.IO;
using System.Reflection;
using System.Threading;

namespace TermThing.Ssh;

/// <summary>
/// Adapter that presents an SSH.NET <see cref="ShellStream"/> as an <see cref="IPtyConnection"/>
/// so the terminal control can treat an SSH session the same way it treats a local PTY.
/// </summary>
public sealed class SshPtyConnection : IPtyConnection
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly BlockingShellReaderStream _readerStream;
    private bool _disposed;
    private int _closedFired;

    /// <summary>
    /// Raised once when the SSH shell exits (either clean exit or abrupt disconnect).
    /// More reliable than <see cref="ProcessExited"/> for SSH connections because
    /// that event is only raised by <see cref="Iciclecreek.Terminal.TerminalControl"/>
    /// when it owns the process launch — which we bypass via reflection.
    /// </summary>
    public event EventHandler? ConnectionClosed;

    public SshPtyConnection(SshClient client, ShellStream shell)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        // Wrap ShellStream in a blocking reader so that ReadAsync never returns 0
        // while the session is alive.  SSH.NET's ShellStream.Read() is non-blocking
        // and returns 0 when its internal buffer is momentarily empty; TerminalView's
        // read loop treats a 0-byte read as EOF and permanently blocks input.
        _readerStream = new BlockingShellReaderStream(shell, client, this);

        // Detect abrupt disconnects (TCP drop, network loss, remote sshd killed).
        _client.ErrorOccurred += OnClientError;
    }

    public Stream ReaderStream => _readerStream;
    public Stream WriterStream => _shell;
    public int Pid => 0;
    public int ExitCode { get; private set; }

    // TerminalView detects clean EOF (Read == 0) on the ShellStream and also listens
    // to this event.  We don't raise it ourselves because Porta.Pty.PtyExitedEventArgs
    // has an internal ctor; instead we signal the reader stream closed which causes
    // ReadAsync to return 0 and TerminalView to handle it as process exit.
#pragma warning disable CS0067
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;
#pragma warning restore CS0067

    private void OnClientError(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
    {
        _readerStream.SignalClosed();
        try { _shell.Close(); } catch { }
        FireConnectionClosed();
    }

    public bool WaitForExit(int milliseconds) => _readerStream.HasClosed || !_client.IsConnected;

    internal void FireConnectionClosed()
    {
        if (Interlocked.CompareExchange(ref _closedFired, 1, 0) == 0)
            ConnectionClosed?.Invoke(this, EventArgs.Empty);
 }

    public void Kill()
    {
        _readerStream.SignalClosed();
        try { _shell.Close(); } catch { }
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
    }

    public void Resize(int cols, int rows)
    {
        // SSH.NET doesn't expose a public window-resize API on ShellStream, so we reach
        // through to the underlying channel via reflection.
        try
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            object? channel = null;

            foreach (var name in new[] { "_channel", "Channel", "_session" })
            {
                var field = typeof(ShellStream).GetField(name, flags);
                if (field != null) { channel = field.GetValue(_shell); break; }
            }

            if (channel == null) return;

            var method = channel.GetType().GetMethod("SendWindowChangeRequest",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: [typeof(uint), typeof(uint), typeof(uint), typeof(uint)],
                modifiers: null);
            method?.Invoke(channel, [(uint)cols, (uint)rows, 0u, 0u]);
        }
        catch
        {
            // Best-effort — remote keeps its previous PTY size if this fails.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.ErrorOccurred -= OnClientError;
        _readerStream.SignalClosed();
        try { _readerStream.Dispose(); } catch { }
        try { _shell.Dispose(); } catch { }
        try { _client.Dispose(); } catch { }
    }

    // -------------------------------------------------------------------------
    // Blocking reader wrapper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Wraps SSH.NET's <see cref="ShellStream"/> so that <see cref="ReadAsync"/>
    /// blocks until data is actually available, rather than returning 0 bytes when
    /// the internal buffer is momentarily empty.
    ///
    /// SSH.NET's <see cref="ShellStream.Read"/> is non-blocking: it returns 0 when
    /// its pipe buffer is empty.  <see cref="TerminalView"/>'s read loop treats a
    /// 0-byte read as end-of-process and permanently disables keyboard input.
    /// This wrapper uses the <see cref="ShellStream.DataReceived"/> event to
    /// suspend cheaply until data arrives, then retries the read.
    /// </summary>
    private sealed class BlockingShellReaderStream : Stream
    {
        private readonly ShellStream _shell;
        private readonly SshClient _client;
        private readonly SshPtyConnection _owner;
        private readonly SemaphoreSlim _dataReady = new(0, int.MaxValue);
        private readonly CancellationTokenSource _closedCts = new();

        public BlockingShellReaderStream(ShellStream shell, SshClient client, SshPtyConnection owner)
        {
            _shell = shell;
            _client = client;
            _owner = owner;
            shell.DataReceived += OnDataReceived;
            // Fired when the remote shell exits cleanly (e.g. user types "exit").
            // Without this, ReadAsync would loop forever: Read() returns 0,
            // IsConnected stays true (TCP is still up), and we wait for data
            // that will never arrive.
            shell.Closed += OnShellClosed;
        }

        private void OnDataReceived(object? sender, EventArgs e)
        {
            try { _dataReady.Release(); } catch { }
        }

        private void OnShellClosed(object? sender, EventArgs e)
        {
            SignalClosed();
            _owner.FireConnectionClosed();
        }

        /// <summary>
        /// Signal that the underlying SSH session has closed.
        /// Any in-progress <see cref="ReadAsync"/> will return 0 (EOF).
        /// </summary>
        public void SignalClosed()
        {
            HasClosed = true;
            try { _closedCts.Cancel(); } catch { }
        }

        /// <summary>
        /// Returns <see langword="true"/> once <see cref="SignalClosed"/> has been called.
        /// Used by <see cref="SshPtyConnection.WaitForExit"/> to correctly report
        /// clean shell exits (where the TCP connection is still alive).
        /// </summary>
        public bool HasClosed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => _shell.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();
        public override void SetLength(long value)
            => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _closedCts.Token);

            while (!linked.IsCancellationRequested)
            {
                // Try a non-blocking read from the ShellStream buffer.
                int read;
                try { read = _shell.Read(buffer, offset, count); }
                catch { return 0; }

                if (read > 0)
                    return read;

                // Buffer is empty — check if the session is actually closed.
                bool isConnected;
                try { isConnected = _client.IsConnected; }
                catch (ObjectDisposedException) { return 0; }
                if (!isConnected)
                    return 0;

                // Wait until SSH.NET signals that new data has arrived.
                try
                {
                    await _dataReady.WaitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }

            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shell.DataReceived -= OnDataReceived;
                _shell.Closed -= OnShellClosed;
                try { _closedCts.Cancel(); } catch { }
                try { _closedCts.Dispose(); } catch { }
                try { _dataReady.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
