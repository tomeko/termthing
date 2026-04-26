using System;
using Avalonia;
using Fonts.Avalonia.CascadiaCode;

namespace TermThing;

class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithCascadiaCodeFont()
            .WithDeveloperTools()
            .LogToTrace();
}
