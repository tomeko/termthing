using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Threading;
using Fonts.Avalonia.CascadiaCode;
using TermThing.Configuration;
using TermThing.Diagnostics;
using TermThing.Updater;

namespace TermThing;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Log.InstallGlobalHandlers();
        Log.Info("app", $"TermThing {VersionHelper.DisplayVersion()} ({VersionHelper.CommitSha() ?? "no sha"}) starting " +
                        $"on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                        $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}){DescribeLinuxSession()}");

        // Opt-in input tracing: run with TERMTHING_INPUT_TRACE=1 to capture every byte
        // written to the PTY (tagged with source) plus IME composition activity into
        // config/input-trace.log. Used to diagnose the interactivity garbage bug.
        if (Environment.GetEnvironmentVariable("TERMTHING_INPUT_TRACE") is { Length: > 0 })
        {
            var logPath = Path.Combine(AppPaths.ConfigDirectory, "input-trace.log");
            Trace.Listeners.Add(new TextWriterTraceListener(logPath));
            Trace.AutoFlush = true;
            Trace.WriteLine($"=== TermThing input trace started {DateTime.Now:O} (pid {Environment.ProcessId}) ===");
        }

        Dispatcher.UIThread.UnhandledException += (_, e) =>
            Log.Error("ui", "Unhandled UI-thread exception", e.Exception);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("app", "Fatal startup/run error", ex);
            throw;
        }
    }

    private static string DescribeLinuxSession()
    {
        if (!OperatingSystem.IsLinux()) return string.Empty;
        var type    = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?";
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?";
        return $", session={type}, desktop={desktop}";
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithCascadiaCodeFont()
            .WithDeveloperTools()
            .AfterSetup(_ => Logger.Sink = new AvaloniaLogSink());
}
