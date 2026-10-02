using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using System.Runtime.InteropServices;
using Avalonia;
using TermThing.Panes;

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

        // TerminalView.OnLoaded auto-launches the process when Process is non-empty and
        // the control enters the visual tree — no explicit LaunchProcess() call needed here.
        // Split panes run the same shell, starting in the folder of the pane they split from.
        TerminalControl CreateTerminal(string? startIn)
        {
            var tc = TerminalFactory.Create(definition, _saveConfig);
            tc.Process = process;
            tc.ProcessArgs = settings.Args;
            if (!string.IsNullOrEmpty(startIn)) tc.StartingDirectory = startIn;
            return tc;
        }

        return Task.FromResult<ISessionInstance>(new LocalSessionInstance(CreateTerminal, definition.Name));
    }

    private static string DefaultShell()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cmd.exe"
            : LocalShells.Default.Process;
}

// ---------------------------------------------------------------------------

internal sealed class LocalSessionInstance : ISessionInstance
{
    private readonly Func<string?, TerminalControl> _createTerminal;
    private readonly PaneLayoutView _panes;
    private readonly PaneTitles _titles;

    public LocalSessionInstance(Func<string?, TerminalControl> createTerminal, string title)
    {
        _createTerminal = createTerminal;
        _titles = new PaneTitles(title, () => Terminal);
        _titles.Changed += (_, _) => TitleChanged?.Invoke(this, EventArgs.Empty);

        var first = createTerminal(null);
        _titles.Watch(first);

        _panes = new PaneLayoutView(first)
        {
            CellSizeProvider = () => new Size(Terminal?.CharWidth ?? 0, Terminal?.CharHeight ?? 0),
            SplitRequested   = Split,
            CloseRequested   = pane => ClosePane((TerminalControl)pane),
        };
        _panes.ActivePaneChanged += (_, _) => _titles.Refresh();
        WatchExit(first);
    }

    public Control TabContent => _panes;
    public TerminalControl? Terminal => _panes.ActivePane as TerminalControl;
    public IReadOnlyList<TerminalControl> Terminals => [.. _panes.Panes.OfType<TerminalControl>()];
    public PaneLayoutView? Panes => _panes;
    public Control? SftpPanel => null;
    public string Title => _titles.Current;
    public event EventHandler? TitleChanged;
    public event EventHandler? SessionEnded;

    private void Split(SplitAxis axis)
    {
        var startIn = Terminal?.CurrentDirectory;
        var tc = _createTerminal(startIn);
        _titles.Watch(tc);
        WatchExit(tc);
        if (!_panes.AddPane(tc, axis))
            try { tc.Kill(); } catch { }
    }

    /// <summary>A pane whose shell exits closes; the session ends with its last pane.</summary>
    private void WatchExit(TerminalControl tc)
    {
        tc.ProcessExited += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!_panes.Panes.Contains(tc)) return;   // already closed from the UI
            if (_panes.PaneCount > 1) { _panes.RemovePane(tc); _titles.Forget(tc); }
            else SessionEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    private void ClosePane(TerminalControl tc)
    {
        if (!_panes.RemovePane(tc)) return;
        _titles.Forget(tc);
        try { tc.Kill(); } catch { }
    }

    public void Kill()
    {
        foreach (var tc in Terminals)
            try { tc.Kill(); } catch { }
    }

    public void Dispose() => Kill();
}
