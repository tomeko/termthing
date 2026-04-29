using Avalonia.Threading;
using Renci.SshNet;
using System.Globalization;
using System.Threading;

namespace TermThing.Ssh;

/// <summary>
/// Snapshot of host vitals produced by <see cref="SysmonPoller"/>. All numeric
/// fields are NaN / 0 when the corresponding metric could not be parsed.
/// </summary>
public sealed record SysmonSnapshot(
    double CpuPercent,
    double MemUsedGb,
    double MemTotalGb,
    double MemPercent,
    string LoadAvg,
    string Uptime,
    double DiskRootPercent,
    bool   Failed,
    string? Error);

/// <summary>
/// Background poller that runs a single combined "/proc/* + df" exec channel
/// against a shared <see cref="SshClient"/> on a fixed interval and parses the
/// result into a <see cref="SysmonSnapshot"/>. Linux-only (parses /proc).
/// Non-Linux hosts will report <c>Failed=true</c>; the panel surfaces that as
/// "(stats unavailable)".
/// </summary>
public sealed class SysmonPoller : IDisposable
{
    private const string Command =
        "LC_ALL=C; cat /proc/stat 2>/dev/null | head -1; echo '---'; " +
        "cat /proc/meminfo 2>/dev/null | head -5; echo '---'; " +
        "cat /proc/loadavg 2>/dev/null; echo '---'; " +
        "cat /proc/uptime 2>/dev/null; echo '---'; " +
        "df -P / 2>/dev/null | tail -1";

    private readonly SshClient _client;
    private readonly Timer _timer;
    private int _ticking;
    private bool _disposed;

    // CPU% needs two samples — keep the previous /proc/stat numbers.
    private long _prevTotal;
    private long _prevIdle;
    private bool _haveCpuSample;

    public event EventHandler<SysmonSnapshot>? SnapshotReceived;

    public SysmonPoller(SshClient client, TimeSpan interval)
    {
        _client = client;
        _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, interval);
    }

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
                using var cmd = _client.CreateCommand(Command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(5);
                output = cmd.Execute();
            }
            catch (Exception ex)
            {
                Raise(Failed("(stats unavailable: " + ex.Message + ")"));
                return;
            }

            var snap = Parse(output);
            Raise(snap);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private void Raise(SysmonSnapshot s)
    {
        var handler = SnapshotReceived;
        if (handler == null) return;
        Dispatcher.UIThread.Post(() => handler.Invoke(this, s));
    }

    private SysmonSnapshot Parse(string output)
    {
        try
        {
            var sections = output.Split("---", StringSplitOptions.None);
            if (sections.Length < 5) return Failed("(stats unavailable)");

            // ---------------- CPU ----------------
            var cpuLine = sections[0].Trim();
            double cpuPct = double.NaN;
            if (cpuLine.StartsWith("cpu ", StringComparison.Ordinal))
            {
                var parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5)
                {
                    long Sum(int from, int to)
                    {
                        long s = 0;
                        for (int i = from; i <= to && i < parts.Length; i++)
                            if (long.TryParse(parts[i], out var v)) s += v;
                        return s;
                    }
                    long total = Sum(1, parts.Length - 1);
                    long idle  = (long.TryParse(parts[4], out var idleV) ? idleV : 0)
                               + (parts.Length > 5 && long.TryParse(parts[5], out var iow) ? iow : 0);

                    if (_haveCpuSample)
                    {
                        long dt = total - _prevTotal;
                        long di = idle  - _prevIdle;
                        if (dt > 0)
                            cpuPct = 100.0 * (dt - di) / dt;
                    }
                    _prevTotal = total;
                    _prevIdle  = idle;
                    _haveCpuSample = true;
                }
            }

            // ---------------- Memory ----------------
            var memLines = sections[1].Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            long memTotalKb = 0, memAvailKb = 0;
            foreach (var ln in memLines)
            {
                var t = ln.Trim();
                if (t.StartsWith("MemTotal:", StringComparison.Ordinal))
                    memTotalKb = ParseMemKb(t);
                else if (t.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    memAvailKb = ParseMemKb(t);
            }
            double memTotalGb = memTotalKb / 1_048_576.0;
            double memUsedGb  = memTotalKb > 0 ? (memTotalKb - memAvailKb) / 1_048_576.0 : 0;
            double memPct     = memTotalKb > 0 ? 100.0 * (memTotalKb - memAvailKb) / memTotalKb : 0;

            // ---------------- Load avg ----------------
            var loadParts = sections[2].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string loadAvg = loadParts.Length >= 3
                ? $"{loadParts[0]} {loadParts[1]} {loadParts[2]}"
                : "--";

            // ---------------- Uptime ----------------
            var upParts = sections[3].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string uptime = "--";
            if (upParts.Length > 0 &&
                double.TryParse(upParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var upSecs))
            {
                uptime = FormatUptime(upSecs);
            }

            // ---------------- Disk ----------------
            var dfParts = sections[4].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double diskPct = 0;
            for (int i = 0; i < dfParts.Length; i++)
            {
                if (dfParts[i].EndsWith('%') &&
                    double.TryParse(dfParts[i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                {
                    diskPct = p;
                    break;
                }
            }

            return new SysmonSnapshot(cpuPct, memUsedGb, memTotalGb, memPct, loadAvg, uptime, diskPct, false, null);
        }
        catch (Exception ex)
        {
            return Failed("(parse error: " + ex.Message + ")");
        }
    }

    private static long ParseMemKb(string line)
    {
        // e.g. "MemTotal:       16334032 kB"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
            if (long.TryParse(parts[i], out var v))
                return v;
        return 0;
    }

    private static string FormatUptime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static SysmonSnapshot Failed(string msg) =>
        new(double.NaN, 0, 0, 0, "--", "--", 0, true, msg);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
