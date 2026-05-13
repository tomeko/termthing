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
/// Background poller that runs a combined SSH command block against a shared
/// <see cref="SshClient"/> on a fixed interval and parses the result into a
/// <see cref="SysmonSnapshot"/>. Supports Linux (via /proc) and macOS (via
/// vm_stat, sysctl, top). Other hosts report <c>Failed=true</c>.
/// </summary>
public sealed class SysmonPoller : IDisposable
{
    // Remote OS, detected once on first tick.
    private enum RemoteOs { Unknown, Linux, MacOs, Other }

    // Linux command bundle — same as the original.
    private const string LinuxCommand =
        "LC_ALL=C; cat /proc/stat 2>/dev/null | head -1; echo '---'; " +
        "cat /proc/meminfo 2>/dev/null | head -5; echo '---'; " +
        "cat /proc/loadavg 2>/dev/null; echo '---'; " +
        "cat /proc/uptime 2>/dev/null; echo '---'; " +
        "df -P / 2>/dev/null | tail -1";

    // macOS command bundle.
    // Sections are separated by "---" echoes, in the same order as Linux so
    // Parse() can dispatch by OS then re-use shared logic where it makes sense.
    //   [0] CPU:     top -l 1 -n 0 line starting with "CPU usage:"
    //   [1] Memory:  vm_stat output + pagesize + total bytes via sysctl
    //   [2] Load:    sysctl vm.loadavg  (format: "{ 1.23 0.45 0.30 }")
    //   [3] Uptime:  sysctl kern.boottime (epoch in sec)
    //   [4] Disk:    df -P /   (cross-platform, same as Linux)
    private const string MacOsCommand =
        "LC_ALL=C; top -l 1 -n 0 | grep 'CPU usage'; echo '---'; " +
        "vm_stat; echo '__MEMSEP__'; sysctl -n hw.memsize; echo '---'; " +
        "sysctl vm.loadavg; echo '---'; " +
        "sysctl kern.boottime; echo '---'; " +
        "df -P / 2>/dev/null | tail -1";

    private readonly SshClient _client;
    private readonly Timer _timer;
    private int _ticking;
    private bool _disposed;
    private RemoteOs _remoteOs = RemoteOs.Unknown;

    // CPU% needs two samples — keep the previous /proc/stat numbers (Linux only).
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

            // Detect remote OS on first tick.
            if (_remoteOs == RemoteOs.Unknown)
                _remoteOs = DetectOs();

            if (_remoteOs == RemoteOs.Other)
            {
                Raise(Failed("(unsupported OS)"));
                return;
            }

            string command = _remoteOs == RemoteOs.MacOs ? MacOsCommand : LinuxCommand;
            string output;
            try
            {
                using var cmd = _client.CreateCommand(command);
                cmd.CommandTimeout = TimeSpan.FromSeconds(8);
                output = cmd.Execute();
            }
            catch (Exception ex)
            {
                Raise(Failed("(stats unavailable: " + ex.Message + ")"));
                return;
            }

            var snap = _remoteOs == RemoteOs.MacOs ? ParseMacOs(output) : ParseLinux(output);
            Raise(snap);
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    private RemoteOs DetectOs()
    {
        try
        {
            using var cmd = _client.CreateCommand("uname -s");
            cmd.CommandTimeout = TimeSpan.FromSeconds(5);
            var result = cmd.Execute().Trim();
            return result switch
            {
                "Linux"  => RemoteOs.Linux,
                "Darwin" => RemoteOs.MacOs,
                _        => RemoteOs.Other,
            };
        }
        catch
        {
            return RemoteOs.Other;
        }
    }

    private void Raise(SysmonSnapshot s)
    {
        var handler = SnapshotReceived;
        if (handler == null) return;
        Dispatcher.UIThread.Post(() => handler.Invoke(this, s));
    }

    // -------------------------------------------------------------------------
    // Linux parser (unchanged from original)
    // -------------------------------------------------------------------------

    private SysmonSnapshot ParseLinux(string output)
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
            double diskPct = ParseDfPercent(dfParts);

            return new SysmonSnapshot(cpuPct, memUsedGb, memTotalGb, memPct, loadAvg, uptime, diskPct, false, null);
        }
        catch (Exception ex)
        {
            return Failed("(parse error: " + ex.Message + ")");
        }
    }

    // -------------------------------------------------------------------------
    // macOS parser
    // -------------------------------------------------------------------------

    private SysmonSnapshot ParseMacOs(string output)
    {
        try
        {
            var sections = output.Split("---", StringSplitOptions.None);
            if (sections.Length < 5) return Failed("(stats unavailable)");

            // ---------------- CPU ----------------
            // top -l 1 -n 0 | grep "CPU usage:" →
            //   "CPU usage: 5.55% user, 11.11% sys, 83.33% idle"
            double cpuPct = double.NaN;
            var cpuLine = sections[0].Trim();
            var idleIdx = cpuLine.IndexOf("idle", StringComparison.OrdinalIgnoreCase);
            if (idleIdx > 0)
            {
                // Walk backwards from "idle" to find the number
                var before = cpuLine[..idleIdx].TrimEnd();
                var pctIdx = before.LastIndexOf(' ');
                var pctStr = before[(pctIdx + 1)..].TrimEnd('%');
                if (double.TryParse(pctStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var idle))
                    cpuPct = Math.Max(0, 100.0 - idle);
            }

            // ---------------- Memory ----------------
            // Section[1] has vm_stat output then "__MEMSEP__" then hw.memsize bytes.
            // vm_stat lines: "Pages free: 12345." / "Pages active: 67890." etc.
            // Page size is always 16384 bytes on Apple Silicon, 4096 on Intel.
            double memTotalGb = 0, memUsedGb = 0, memPct = 0;
            var memSection = sections[1];
            var sepIdx = memSection.IndexOf("__MEMSEP__", StringComparison.Ordinal);
            if (sepIdx > 0)
            {
                var vmStatText = memSection[..sepIdx];
                var memSizeText = memSection[(sepIdx + 10)..].Trim();

                long pageSizeBytes = 16384; // Apple Silicon default
                long freePages = 0, activePages = 0, inactivePages = 0, wiredPages = 0, compressedPages = 0;
                foreach (var ln in vmStatText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ln.StartsWith("Mach Virtual Memory Statistics", StringComparison.Ordinal))
                    {
                        // "page size of 4096 bytes"
                        var psIdx = ln.IndexOf("page size of ", StringComparison.Ordinal);
                        if (psIdx >= 0)
                        {
                            var rest = ln[(psIdx + 13)..];
                            var spaceIdx = rest.IndexOf(' ');
                            if (spaceIdx > 0 && long.TryParse(rest[..spaceIdx], out var ps))
                                pageSizeBytes = ps;
                        }
                        continue;
                    }
                    ParseVmStatLine(ln, "Pages free:", ref freePages);
                    ParseVmStatLine(ln, "Pages active:", ref activePages);
                    ParseVmStatLine(ln, "Pages inactive:", ref inactivePages);
                    ParseVmStatLine(ln, "Pages wired down:", ref wiredPages);
                    ParseVmStatLine(ln, "Pages occupied by compressor:", ref compressedPages);
                }

                if (long.TryParse(memSizeText, out var totalBytes))
                {
                    long usedBytes = (activePages + wiredPages + compressedPages) * pageSizeBytes;
                    memTotalGb = totalBytes / 1_073_741_824.0;
                    memUsedGb  = usedBytes  / 1_073_741_824.0;
                    memPct     = totalBytes > 0 ? 100.0 * usedBytes / totalBytes : 0;
                }
            }

            // ---------------- Load avg ----------------
            // "vm.loadavg: { 0.42 0.38 0.31 }"
            string loadAvg = "--";
            var loadLine = sections[2].Trim();
            var lbrace = loadLine.IndexOf('{');
            var rbrace = loadLine.IndexOf('}');
            if (lbrace >= 0 && rbrace > lbrace)
            {
                var nums = loadLine[(lbrace + 1)..rbrace].Trim()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 3)
                    loadAvg = $"{nums[0]} {nums[1]} {nums[2]}";
            }

            // ---------------- Uptime ----------------
            // "kern.boottime: { sec = 1715000000, usec = 0 } Mon May 12 ..."
            string uptime = "--";
            var bootLine = sections[3].Trim();
            var secIdx = bootLine.IndexOf("sec = ", StringComparison.Ordinal);
            if (secIdx >= 0)
            {
                var rest = bootLine[(secIdx + 6)..];
                var commaIdx = rest.IndexOf(',');
                var secStr = commaIdx > 0 ? rest[..commaIdx] : rest;
                if (long.TryParse(secStr.Trim(), out var bootEpoch))
                {
                    var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    uptime = FormatUptime(nowEpoch - bootEpoch);
                }
            }

            // ---------------- Disk ----------------
            var dfParts = sections[4].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double diskPct = ParseDfPercent(dfParts);

            return new SysmonSnapshot(cpuPct, memUsedGb, memTotalGb, memPct, loadAvg, uptime, diskPct, false, null);
        }
        catch (Exception ex)
        {
            return Failed("(parse error: " + ex.Message + ")");
        }
    }

    private static void ParseVmStatLine(string line, string prefix, ref long value)
    {
        if (!line.TrimStart().StartsWith(prefix, StringComparison.Ordinal)) return;
        var rest = line[(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length)..].Trim().TrimEnd('.');
        if (long.TryParse(rest, out var v)) value = v;
    }

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    private static long ParseMemKb(string line)
    {
        // e.g. "MemTotal:       16334032 kB"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i < parts.Length; i++)
            if (long.TryParse(parts[i], out var v))
                return v;
        return 0;
    }

    private static double ParseDfPercent(string[] parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].EndsWith('%') &&
                double.TryParse(parts[i].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                return p;
        }
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
