using System.Text;
using Avalonia.Logging;
using TermThing.Configuration;

namespace TermThing.Diagnostics;

public enum LogLevel { Info, Warn, Error }

/// <summary>
/// Minimal application log: one file per day under <see cref="AppPaths.LogsDirectory"/>,
/// pruned to the last <see cref="RetentionDays"/> days. Writes are synchronous and
/// flushed per line — volume is low, and <c>Environment.Exit</c> on window close
/// would drop anything still buffered by a background writer.
///
/// <para>Never pass secrets (passwords, passphrases, key material) into a message.</para>
/// </summary>
public static class Log
{
    private const int RetentionDays = 7;

    private static readonly object _lock = new();
    private static StreamWriter? _writer;
    private static DateOnly _writerDate;
    private static bool _disabled;

    public static void Info(string category, string message) => Write(LogLevel.Info, category, message, null);
    public static void Warn(string category, string message, Exception? ex = null) => Write(LogLevel.Warn, category, message, ex);
    public static void Error(string category, string message, Exception? ex = null) => Write(LogLevel.Error, category, message, ex);

    public static void Write(LogLevel level, string category, string message, Exception? ex)
    {
        var now = DateTime.Now;
        var sb = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(level switch { LogLevel.Warn => " WRN ", LogLevel.Error => " ERR ", _ => " INF " })
            .Append('[').Append(category).Append("] ")
            .Append(message);
        if (ex is not null)
            sb.AppendLine().Append("    ").Append(ex.ToString().Replace("\n", "\n    "));
        var line = sb.ToString();

        System.Diagnostics.Debug.WriteLine(line);

        lock (_lock)
        {
            if (_disabled) return;
            try
            {
                EnsureWriter(DateOnly.FromDateTime(now));
                _writer!.WriteLine(line);
            }
            catch
            {
                // Logging must never take the app down (read-only install dir, disk full, ...).
                _disabled = true;
            }
        }
    }

    private static void EnsureWriter(DateOnly today)
    {
        if (_writer is not null && _writerDate == today) return;

        _writer?.Dispose();
        var dir = AppPaths.LogsDirectory;
        var path = Path.Combine(dir, $"termthing-{today:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writerDate = today;
        Prune(dir, today);
    }

    private static void Prune(string dir, DateOnly today)
    {
        var cutoff = today.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(dir, "termthing-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["termthing-".Length..];
            if (DateOnly.TryParseExact(stamp, "yyyyMMdd", out var date) && date < cutoff)
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    /// <summary>Hooks process-wide unhandled exception sources into the log.</summary>
    public static void InstallGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Error("crash", $"Unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Warn("task", "Unobserved task exception", e.Exception);
    }
}

/// <summary>Forwards Avalonia's own warnings and errors into <see cref="Log"/>.</summary>
public sealed class AvaloniaLogSink : ILogSink
{
    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        => Log(level, area, source, messageTemplate, []);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area)) return;
        var message = propertyValues.Length == 0
            ? messageTemplate
            : $"{messageTemplate} | {string.Join(", ", propertyValues)}";
        if (source is not null)
            message += $" (source: {source.GetType().Name})";
        Diagnostics.Log.Write(level >= LogEventLevel.Error ? LogLevel.Error : LogLevel.Warn,
            $"avalonia:{area}", message, null);
    }
}
