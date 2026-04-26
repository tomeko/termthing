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

    public void Cancel()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
    }

    private async Task RunAsync()
    {
        while (_jobs.TryDequeue(out var job))
        {
            if (_cts.IsCancellationRequested)
                break;

            try
            {
                if (job.IsUpload)
                    await UploadAsync(job, _cts.Token);
                else
                    await DownloadAsync(job, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                TransferError?.Invoke(this, $"{Path.GetFileName(job.LocalPath)}: {ex.Message}");
            }
        }

        QueueEmpty?.Invoke(this, EventArgs.Empty);
    }

    private Task UploadAsync(TransferJob job, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var fs = File.OpenRead(job.LocalPath);
            _sftp.UploadFile(fs, job.RemotePath, canOverride: true,
                transferred =>
                {
                    ct.ThrowIfCancellationRequested();
                    ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                    {
                        FileName         = Path.GetFileName(job.LocalPath),
                        TransferredBytes = (long)transferred,
                        TotalBytes       = job.TotalBytes,
                        IsUpload         = true,
                    });
                });
        }, ct);
    }

    private Task DownloadAsync(TransferJob job, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.LocalPath)!);
            using var fs = File.Create(job.LocalPath);
            _sftp.DownloadFile(job.RemotePath, fs,
                transferred =>
                {
                    ct.ThrowIfCancellationRequested();
                    ProgressChanged?.Invoke(this, new TransferProgressEventArgs
                    {
                        FileName         = Path.GetFileName(job.LocalPath),
                        TransferredBytes = (long)transferred,
                        TotalBytes       = job.TotalBytes,
                        IsUpload         = false,
                    });
                });
        }, ct);
    }

    public void Dispose() => _cts.Dispose();
}
