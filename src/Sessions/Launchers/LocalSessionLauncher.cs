using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using System.Runtime.InteropServices;
using TermThing.Configuration;
using TermThing.Views;

namespace TermThing.Sessions.Launchers;

public sealed class LocalSessionLauncher : ISessionLauncher
{
    private readonly Action? _saveConfig;

    public LocalSessionLauncher(Action? saveConfig = null)
    {
        _saveConfig = saveConfig;
    }

    public SessionKind Kind => SessionKind.Local;

    public Task<ISessionInstance> LaunchAsync(
        SessionDefinition definition,
        ISessionPromptHost promptHost,
        CancellationToken cancellationToken = default)
    {
        var settings = definition.Settings as LocalSettings
            ?? throw new InvalidOperationException("LocalSettings required.");

        var process = string.IsNullOrWhiteSpace(settings.Process)
            ? DefaultShell()
            : settings.Process;

        // Use the persisted font size if one has been set via Ctrl+Wheel
        var fontSize = SettingsService.Temp.TerminalFontSize > 0
            ? SettingsService.Temp.TerminalFontSize
            : 14;

        var tc = new TerminalControl
        {
            Process = process,
            Args = settings.Args,
            Background = Brushes.Black,
            Foreground = Brushes.LightGray,
            FontFamily = FontFamily.Parse("fonts:CascadiaCode#Cascadia Code"),
            FontSize = fontSize,
        };

        // Attach terminal mouse enhancements (Ctrl+RightClick menu, Ctrl+Wheel font size)
        TerminalContextMenuBehavior.Attach(tc, definition, _saveConfig);

        // TerminalView.OnLoaded auto-launches the process when Process is non-empty and
        // the control enters the visual tree — no explicit LaunchProcess() call needed here.
        return Task.FromResult<ISessionInstance>(new LocalSessionInstance(tc, definition.Name));
    }

    private static string DefaultShell()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cmd.exe"
            : LocalShells.Default.Process;
}

// ---------------------------------------------------------------------------

internal sealed class LocalSessionInstance : ISessionInstance
{
    private readonly TerminalControl _tc;

    public LocalSessionInstance(TerminalControl tc, string title)
    {
        _tc = tc;
        Title = title;

        TerminalView.AddTitleChangedHandler(_tc, (_, e) =>
        {
            if (!e.Handled) { Title = e.Title; e.Handled = true; }
        });

        _tc.ProcessExited += (_, _) =>
            Dispatcher.UIThread.Post(() => SessionEnded?.Invoke(this, EventArgs.Empty));
    }

    public Control TabContent => _tc;
    public TerminalControl? Terminal => _tc;
    public Control? SftpPanel => null;
    public string Title { get; private set; }
    public event EventHandler? SessionEnded;

    public void Kill()
    {
        try { _tc.Kill(); } catch { }
    }

    public void Dispose() => Kill();
}
