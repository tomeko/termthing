using Renci.SshNet;
using System.Collections.Concurrent;

namespace TermThing.Sftp;

/// <summary>
/// Sequential file-transfer queue backed by SSH.NET.
/// Fires <see cref="ProgressChanged"/> on every byte-progress callback
/// and <see cref="QueueEmpty"/> when the last job completes.
/// Thread-safe: enqueue from UI thread, runs transfers on a background thread.
/// </summary>
public sealed class TransferQueue : IDisposable
{
    public event EventHandler<TransferProgressEventArgs>? ProgressChanged;
    public event EventHandler? QueueEmpty;
    public event EventHandler<string>? TransferError;

    private readonly SftpClient _sftp;
    private readonly ConcurrentQueue<TransferJob> _jobs = new();
    private CancellationTokenSource _cts = new();
    private Task _worker = Task.CompletedTask;
    private readonly object _lock = new();

    public TransferQueue(SftpClient sftp)
    {
        _sftp = sftp;
    }

    public void Enqueue(IEnumerable<TransferJob> jobs)
    {
        foreach (var job in jobs)
            _jobs.Enqueue(job);

        lock (_lock)
        {
            if (_worker.IsCompleted)
                _worker = Task.Run(RunAsync);
        }
    }

    /// <summary>
    /// Stops the running transfer and drops everything still queued. Without the drain,
    /// the rest of a cancelled batch would silently resume on the next, unrelated enqueue.
    /// </summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _jobs.Clear();
            _cts.Cancel();
            _cts = new CancellationTokenSource();
        }
    }

    private async Task RunAsync()
    {
        while (_jobs.TryDequeue(out var job))
        {
            CancellationToken ct;
            lock (_lock) ct = _cts.Token;

            try
            {
                if (job.IsUpload)
                    await UploadAsync(job, ct);
                else
                    await DownloadAsync(job, ct);
            }
            catch (OperationCanceledException)
            {
                TransferError?.Invoke(this, $"{Path.GetFileName(job.LocalPath)}: cancelled");
            }
            catch (Exception ex)
            {
                TransferError?.Invoke(this, $"{Path.GetFileName(job.LocalPath)}: {ex.Message}");
            }
        }

        QueueEmpty?.Invoke(this, EventArgs.Empty);
    }

    // Cancellation and progress ride on a stream wrapper rather than SSH.NET's progress
    // callback: SSH.NET invokes that callback on a thread-pool thread of its own, so an
    // exception thrown there to cancel is unhandled and takes the whole process down.
    // The wrapper's Read/Write run on the transferring thread, where a throw unwinds
    // UploadFile/DownloadFile normally.

    private Task UploadAsync(TransferJob job, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(job.LocalPath);
                using var progress = new ProgressStream(fs, ct, transferred => ReportProgress(job, transferred));
                _sftp.UploadFile(progress, job.RemotePath, canOverride: true);
            }
            catch (OperationCanceledException)
            {
                // Don't leave a truncated file on the server that looks like a finished upload.
                // (Overwriting an existing file truncated it as soon as the upload began, so
                // there is nothing intact left to keep either way.)
                try { _sftp.DeleteFile(job.RemotePath); } catch { }
                throw;
            }
        }, ct);
    }

    private Task DownloadAsync(TransferJob job, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.LocalPath)!);
            try
            {
                using var fs = File.Create(job.LocalPath);
                using var progress = new ProgressStream(fs, ct, transferred => ReportProgress(job, transferred));
                _sftp.DownloadFile(job.RemotePath, progress);
            }
            catch (OperationCanceledException)
            {
                // Don't leave a truncated file behind that looks like a finished download.
                try { File.Delete(job.LocalPath); } catch { }
                throw;
            }
        }, ct);
    }

    private void ReportProgress(TransferJob job, long transferred) =>
        ProgressChanged?.Invoke(this, new TransferProgressEventArgs
        {
            FileName         = Path.GetFileName(job.LocalPath),
            TransferredBytes = transferred,
            TotalBytes       = job.TotalBytes,
            IsUpload         = job.IsUpload,
        });

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }

    /// <summary>
    /// Pass-through stream that counts bytes and throws <see cref="OperationCanceledException"/>
    /// from Read/Write once <paramref name="ct"/> is cancelled.
    /// </summary>
    private sealed class ProgressStream(Stream inner, CancellationToken ct, Action<long> onProgress) : Stream
    {
        private long _transferred;

        public override bool CanRead  => inner.CanRead;
        public override bool CanSeek  => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length   => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            int n = inner.Read(buffer, offset, count);
            Advance(n);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            inner.Write(buffer, offset, count);
            Advance(count);
        }

        private void Advance(int n)
        {
            if (n <= 0) return;
            _transferred += n;
            onProgress(_transferred);
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
    }
}
