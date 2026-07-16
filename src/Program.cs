using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Fonts.Avalonia.CascadiaCode;
using TermThing.Configuration;

namespace TermThing;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithCascadiaCodeFont()
            .WithDeveloperTools()
            .LogToTrace();
}
