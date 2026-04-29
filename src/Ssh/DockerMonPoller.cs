using Avalonia.Threading;
using Renci.SshNet;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace TermThing.Ssh;

/// <summary>
/// One row in the DockerMon DataGrid. Mutable so the poller can update fields
/// in place when re-merging snapshots, preserving DataGrid selection.
/// </summary>
public sealed class DockerContainerRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public string Id { get; init; } = string.Empty;

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _image = string.Empty;
    public string Image { get => _image; set => Set(ref _image, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _ports = string.Empty;
    public string Ports
    {
        get => _ports;
        set
        {
            if (Equals(_ports, value)) return;
            _ports = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Ports)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HostPorts)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContainerPorts)));
        }
    }

    /// <summary>Unique host-side ports extracted from the raw docker Ports field, e.g. "8080, 8443".</summary>
    public string HostPorts => ParseHostPorts(_ports);

    /// <summary>Unique container-side ports/protocols extracted from the raw docker Ports field, e.g. "80/tcp, 443/tcp".</summary>
    public string ContainerPorts => ParseContainerPorts(_ports);

    private static string ParseHostPorts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var host = part[..arrow];
            var colon = host.LastIndexOf(':');
            var port = colon >= 0 ? host[(colon + 1)..] : host;
            if (seen.Add(port)) result.Add(port);
        }
        return string.Join(", ", result);
    }

    private static string ParseContainerPorts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var arrow = part.IndexOf("->", StringComparison.Ordinal);
            if (arrow < 0) continue;
            var container = part[(arrow + 2)..]; // e.g. "80/tcp"
            if (seen.Add(container)) result.Add(container);
        }
        return string.Join(", ", result);
    }

    private bool _busy;
    /// <summary>True while a stop/restart command is in flight; binds button IsEnabled.</summary>
    public bool Busy { get => _busy; set { Set(ref _busy, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotBusy))); } }
    public bool NotBusy => !_busy;
}

/// <summary>
/// Background poller for <c>docker ps</c>. Detects docker presence on demand,
/// then polls on a fixed interval and merges results into a shared
/// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> by container ID
/// so DataGrid selection is preserved across refreshes.
/// </summary>
public sealed class DockerMonPoller : IDisposable
{
    private readonly SshClient _client;
    private readonly TimeSpan _interval;
    private Timer? _timer;
    private int _ticking;
    private bool _disposed;

    /// <summary>If true, runs <c>docker ps -a</c> instead of just running containers.</summary>
    public bool ShowAll { get; set; }

    /// <summary>Fired with the latest list (UI thread). Consumer merges into its collection.</summary>
    public event EventHandler<IReadOnlyList<DockerContainerRow>>? Snapshot;

    /// <summary>Fired with a one-shot error message when polling or actions fail (UI thread).</summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Fired (UI thread) when the user clicks the Logs button for a container row.
    /// The subscriber is responsible for opening a <c>LogTailWindow</c>.
    /// </summary>
    public event EventHandler<DockerContainerRow>? LogsRequested;

    /// <summary>Raises <see cref="LogsRequested"/> for the given row (must be called on UI thread).</summary>
    internal void RequestLogs(DockerContainerRow row) =>
        LogsRequested?.Invoke(this, row);

    public DockerMonPoller(SshClient client, TimeSpan interval)
    {
        _client = client;
        _interval = interval;
    }

    /// <summary>
    /// Probes for docker via <c>docker --version</c> with a 5 s timeout.
    /// Returns true if docker is available; false otherwise.
    /// Safe to call from a background thread.
    /// </summary>
    public Task<bool> ProbeAsync()
    {
        return Task.Run(() =>
        {
            try
            {
                if (!_client.IsConnected) return false;
                using var cmd = _client.CreateCommand("command -v docker >/dev/null 2>&1 && docker --version 2>/dev/null");
                cmd.CommandTimeout = TimeSpan.FromSeconds(5);
                var output = cmd.Execute();
                return cmd.ExitStatus == 0 && !string.IsNullOrWhiteSpace(output);
            }
            catch
            {
                return false;
            }
        });
    }

    public void Start()
    {
        if (_disposed || _timer != null) return;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, _interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void RefreshNow() => Task.Run(Tick);

    private void Tick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (!_client.IsConnected) return;
            string output;
            try
            {
                var arg = ShowAll ? "-a" : "";
                using var cmd = _client.CreateCommand(
                    $"docker ps {arg} --no-trunc --format '{{{{.ID}}}}\\t{{{{.Names}}}}\\t{{{{.Image}}}}\\t{{{{.Status}}}}\\t{{{{.Ports}}}}'");
                cmd.CommandTimeout = TimeSpan.FromSeconds(8);
                output = cmd.Execute();
                if (cmd.ExitStatus != 0)
                {
                    RaiseError($"docker ps failed: {cmd.Error?.Trim()}");
                    return;
                }
            }
            catch (Exception ex)
            {
                RaiseError($"docker ps error: {ex.Message}");
                return;
            }

            var rows = new List<DockerContainerRow>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 5) continue;
                rows.Add(new DockerContainerRow
                {
                    Id     = parts[0],
                    Name   = parts[1],
                    Image  = parts[2],
                    Status = parts[3],
                    Ports  = parts[4],
                });
            }

            var handler = Snapshot;
            if (handler != null)
                Dispatcher.UIThread.Post(() => handler.Invoke(this, rows));
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// Runs <c>docker stop &lt;id&gt;</c>. Returns true on success.
    /// </summary>
    public Task<bool> StopAsync(string containerId, CancellationToken ct = default) =>
        RunActionAsync($"docker stop {Shell.QuoteId(containerId)}", TimeSpan.FromSeconds(20), ct);

    /// <summary>
    /// Runs <c>docker restart &lt;id&gt;</c>. Returns true on success.
    /// </summary>
    public Task<bool> RestartAsync(string containerId, CancellationToken ct = default) =>
        RunActionAsync($"docker restart {Shell.QuoteId(containerId)}", TimeSpan.FromSeconds(20), ct);

    private Task<bool> RunActionAsync(string cmdLine, TimeSpan timeout, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!_client.IsConnected) return false;
                using var cmd = _client.CreateCommand(cmdLine);
                cmd.CommandTimeout = timeout;
                cmd.Execute();
                if (cmd.ExitStatus != 0)
                {
                    RaiseError($"{cmdLine}: {cmd.Error?.Trim()}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                RaiseError($"{cmdLine}: {ex.Message}");
                return false;
            }
        }, ct);
    }

    private void RaiseError(string msg)
    {
        var h = Error;
        if (h != null) Dispatcher.UIThread.Post(() => h.Invoke(this, msg));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static class Shell
    {
        // Container IDs are hex; restrict to a safe charset so we can pass through unquoted.
        public static string QuoteId(string id)
        {
            foreach (var c in id)
                if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                    return "'" + id.Replace("'", "'\\''") + "'";
            return id;
        }
    }
}
