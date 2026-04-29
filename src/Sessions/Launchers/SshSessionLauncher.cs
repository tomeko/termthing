using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.Diagnostics;
using System.IO;
using TermThing.Configuration;
using TermThing.Editor;
using TermThing.Ssh;
using TermThing.Views;

namespace TermThing.Sessions.Launchers;

public sealed class SshSessionLauncher : ISessionLauncher
{
    private readonly IKnownHostsService _knownHosts;
    private readonly Func<AppConfig> _getConfig;
    private readonly EditorRegistry? _editors;
    private readonly Action? _saveConfig;

    public SshSessionLauncher(IKnownHostsService knownHosts, Func<AppConfig> getConfig, EditorRegistry? editors = null, Action? saveConfig = null)
    {
        _knownHosts = knownHosts;
        _getConfig  = getConfig;
        _editors    = editors;
        _saveConfig = saveConfig;
    }

    public SessionKind Kind => SessionKind.Ssh;

    public async Task<ISessionInstance> LaunchAsync(
        SessionDefinition definition,
        ISessionPromptHost promptHost,
        CancellationToken cancellationToken = default)
    {
        var settings = definition.Settings as SshSettings
            ?? throw new InvalidOperationException("SshSettings required.");

        // Show the full credentials dialog only when there's nothing to work with —
        // no key file and no cached password. Key-only sessions go straight to connect;
        // if the key turns out to be encrypted we'll prompt for just the passphrase below.
        bool needsPrompt = string.IsNullOrWhiteSpace(settings.KeyFilePath)
                        && string.IsNullOrEmpty(settings.TransientPassword);
        if (needsPrompt)
        {
            var confirmed = await promptHost.PromptForSshSecretsAsync(definition);
            if (!confirmed) throw new OperationCanceledException("User cancelled SSH login.");
            settings = (SshSettings)definition.Settings!;
        }

        // If a key file is configured and we don't have a passphrase yet, probe it now.
        // SSH.NET throws SshPassPhraseNullOrEmptyException when the key is encrypted
        // but no passphrase was supplied; show a minimal passphrase popup in that case.
        if (!string.IsNullOrWhiteSpace(settings.KeyFilePath) &&
            string.IsNullOrEmpty(settings.TransientKeyPassphrase))
        {
            try
            {
                _ = new PrivateKeyFile(settings.KeyFilePath);
                // key opened without passphrase — fine, continue
            }
            catch (Exception ex) when (
                ex is SshPassPhraseNullOrEmptyException ||
                ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            {
                var passphrase = await promptHost.PromptForPassphraseAsync(settings.KeyFilePath, settings.Host);
                if (passphrase is null)
                    throw new OperationCanceledException("User cancelled passphrase entry.");
                settings.TransientKeyPassphrase = passphrase;
            }
        }

        // ----------------------------------------------------------------
        // Resolve jump-host chain (empty = direct connect).
        // ----------------------------------------------------------------
        var hops = JumpHostResolver.Resolve(settings, _getConfig());

        SshClient client;
        SshChainResult? chainResult = null;

        if (hops.Count == 0)
        {
            // --- Direct connection (existing path) ---
            var connectionInfo = SshConnectionInfoFactory.Build(
                settings.Host, settings.Port, settings.Username,
                settings.KeyFilePath, settings.TransientKeyPassphrase, settings.TransientPassword);

            HostKeyEventArgs? pendingKeyArgs = null;
            KnownHostStatus?  pendingStatus  = null;

            client = new SshClient(connectionInfo);
            client.HostKeyReceived += (_, e) =>
            {
                var status = _knownHosts.Check(settings.Host, settings.Port, e);
                if (status == KnownHostStatus.Trusted) { e.CanTrust = true; return; }
                e.CanTrust    = false;
                pendingKeyArgs = e;
                pendingStatus  = status;
            };

            var connectSw = Stopwatch.StartNew();
            await Task.Run(() => { try { client.Connect(); } catch { } }, cancellationToken);
            Debug.WriteLine($"[SSH] Connect: {connectSw.ElapsedMilliseconds}ms  kex={connectionInfo.CurrentKeyExchangeAlgorithm}  cipher={connectionInfo.CurrentServerEncryption}");

            if (pendingKeyArgs is not null)
            {
                var (action, args) = await PromptHostKeyAsync(settings.Host, settings.Port,
                    pendingStatus!.Value, pendingKeyArgs);

                if (action == HostKeyAction.Cancel)
                {
                    client.Dispose();
                    throw new OperationCanceledException("SSH connection aborted by user.");
                }
                if (action == HostKeyAction.TrustAndConnect)
                    _knownHosts.Trust(settings.Host, settings.Port, args);

                client.Dispose();
                client = new SshClient(connectionInfo);
                client.HostKeyReceived += (_, e) => e.CanTrust = true;
                await Task.Run(() => client.Connect(), cancellationToken);
            }
        }
        else
        {
            // --- Jump-host path ---
            chainResult = await SshChainConnector.ConnectAsync(
                hops, settings, _knownHosts, promptHost,
                progress: msg => Debug.WriteLine($"[SSH jump] {msg}"),
                cancellationToken: cancellationToken);
            client = chainResult.FinalClient;
        }

        // ----------------------------------------------------------------
        // SFTP — needs a separate client through its own forward (or direct).
        // ----------------------------------------------------------------
        SftpClient? sftpClient     = null;
        Task?       sftpConnectTask = null;
        if (settings.EnableSftp)
        {
            var sftpSw = Stopwatch.StartNew();
            if (chainResult is null)
            {
                // Direct — reuse the same ConnectionInfo as the shell client.
                var directCi = SshConnectionInfoFactory.Build(
                    settings.Host, settings.Port, settings.Username,
                    settings.KeyFilePath, settings.TransientKeyPassphrase, settings.TransientPassword);
                sftpClient = new SftpClient(directCi);
            }
            else
            {
                // Tunnelled — open a new forward on the last jump client so SFTP
                // gets its own independent TCP stream.
                var lastJump = chainResult.JumpClients.Count > 0
                    ? chainResult.JumpClients[^1]
                    : null;
                if (lastJump is not null)
                {
                    var sftpFwd = new Renci.SshNet.ForwardedPortLocal(
                        System.Net.IPAddress.Loopback.ToString(), (uint)0,
                        settings.Host, (uint)settings.Port);
                    lastJump.AddForwardedPort(sftpFwd);
                    sftpFwd.Start();
                    var sftpCi = SshConnectionInfoFactory.Build(
                        System.Net.IPAddress.Loopback.ToString(), (int)sftpFwd.BoundPort,
                        settings.Username, settings.KeyFilePath,
                        settings.TransientKeyPassphrase, settings.TransientPassword);
                    sftpClient = new SftpClient(sftpCi);
                    // Store the extra forward so it is disposed with the chain.
                    chainResult = chainResult with
                    {
                        Forwards = [.. chainResult.Forwards, sftpFwd],
                    };
                }
                else
                {
                    // Edge case: chain resolved but no jump clients (shouldn't happen).
                    sftpClient = new SftpClient(chainResult.FinalConnectionInfo);
                }
            }

            sftpConnectTask = Task.Run(() => sftpClient.Connect(), cancellationToken)
                .ContinueWith(_ => Debug.WriteLine($"[SSH] SFTP connect: {sftpSw.ElapsedMilliseconds}ms"),
                    TaskScheduler.Default);
        }

        // ----------------------------------------------------------------
        // Build terminal control and launch.
        // ----------------------------------------------------------------
        var fontSize = SettingsService.Temp.TerminalFontSize > 0
            ? SettingsService.Temp.TerminalFontSize
            : 14;

        var tc = new TerminalControl
        {
            Process    = string.Empty,
            Background = Brushes.Black,
            Foreground = Brushes.LightGray,
            FontFamily = FontFamily.Parse("fonts:CascadiaCode#Cascadia Code"),
            FontSize   = fontSize,
        };

        TerminalContextMenuBehavior.Attach(tc);

        if (!tc.IsLoaded)
        {
            var tcs = new TaskCompletionSource<bool>();
            tc.Loaded += (_, _) => tcs.TrySetResult(true);
            var instance = new SshSessionInstance(tc, definition.Name, client, sftpClient, sftpConnectTask, chainResult, _editors, definition, _saveConfig);
            _ = CompleteConnectionAsync(tc, client, sftpClient, settings, tcs.Task, instance);
            return instance;
        }

        var loadedInstance = new SshSessionInstance(tc, definition.Name, client, sftpClient, sftpConnectTask, chainResult, _editors, definition, _saveConfig);
        await CompleteConnectionAsync(tc, client, sftpClient, settings, Task.CompletedTask, loadedInstance);
        return loadedInstance;
    }

    private static async Task CompleteConnectionAsync(
        TerminalControl tc,
        SshClient client,
        SftpClient? sftpClient,
        SshSettings settings,
        Task loadedTask,
        SshSessionInstance instance)
    {
        await loadedTask;
        var cols = (uint)Math.Max(80, tc.Terminal.Cols);
        var rows = (uint)Math.Max(24, tc.Terminal.Rows);
        var shellSw = Stopwatch.StartNew();
        var shell = client.CreateShellStream(settings.Term, cols, rows, 0, 0, 0x10000);
        Debug.WriteLine($"[SSH] CreateShellStream: {shellSw.ElapsedMilliseconds}ms");
        instance.SetShellCommand(line => shell.WriteLine(line));
        var connection = new SshPtyConnection(client, shell);
        instance.OnPtyConnectionReady(connection);
        await tc.AttachConnection(connection);

        if (sftpClient != null && settings.ShellIntegrationOsc7)
        {
            await Task.Run(() => shell.WriteLine(
                "PROMPT_COMMAND='printf \"\\033]7;file://${HOSTNAME}${PWD}\\007\"'"));
        }
    }

    private static async Task<(HostKeyAction, HostKeyEventArgs)> PromptHostKeyAsync(
        string host, int port, KnownHostStatus status, HostKeyEventArgs args)
    {
        var dialog = new HostKeyPromptDialog(host, port, status, args);
        var action = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var owner = Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            return dialog.ShowDialog<HostKeyAction?>(owner!);
        });
        return (action ?? HostKeyAction.Cancel, args);
    }
}

internal sealed class SshSessionInstance : ISessionInstance
{
    private readonly TerminalControl _tc;
    private readonly Grid _hostPanel;
    private readonly ContentControl _sysmonSlot;
    private readonly ContentControl _dockerMonSlot;
    private readonly GridSplitter _dockerSplitter;
    private readonly RowDefinition _dockerMonRowDef;
    private double _dockerMonHeight = 200;
    private readonly SshClient _client;
    private readonly SftpClient? _sftpClient;
    private readonly SftpFileBrowserView? _sftpView;
    private readonly SshChainResult? _chain;
    private readonly SessionDefinition? _definition;
    private readonly Action? _saveConfig;

    private SysmonPoller? _sysmonPoller;
    private SysmonPanel? _sysmonPanel;
    private DockerMonPoller? _dockerMonPoller;
    private DockerMonPanel? _dockerMonPanel;

    private readonly List<LogTailWindow> _tailWindows = new();
    private bool _connectionEnded;

    public SshSessionInstance(
        TerminalControl  tc,
        string           title,
        SshClient        client,
        SftpClient?      sftpClient,
        Task?            sftpConnectTask = null,
        SshChainResult?  chain           = null,
        EditorRegistry?  editors         = null,
        SessionDefinition? definition    = null,
        Action?          saveConfig      = null)
    {
        _chain = chain;
        _tc = tc;
        _client = client;
        _sftpClient = sftpClient;
        _definition = definition;
        _saveConfig = saveConfig;
        Title = title;

        // Wrap the terminal in a Grid so DockerMon and Sysmon rows can be
        // docked below with a resizable GridSplitter above DockerMon.
        // Row layout: 0=terminal(*), 1=docker-splitter(Auto), 2=docker-panel(pixel), 3=sysmon(Auto).
        _sysmonSlot    = new ContentControl { IsVisible = false };
        _dockerMonSlot = new ContentControl { IsVisible = false };
        _dockerMonRowDef = new RowDefinition(new GridLength(0, GridUnitType.Pixel));
        _dockerSplitter = new GridSplitter
        {
            Height = 5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
            Background = new SolidColorBrush(Color.Parse("#3c3c3c")),
            IsVisible = false,
        };
        _hostPanel = new Grid();
        _hostPanel.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        _hostPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _hostPanel.RowDefinitions.Add(_dockerMonRowDef);
        _hostPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(_tc, 0);
        Grid.SetRow(_dockerSplitter, 1);
        Grid.SetRow(_dockerMonSlot, 2);
        Grid.SetRow(_sysmonSlot, 3);
        _hostPanel.Children.Add(_tc);
        _hostPanel.Children.Add(_dockerSplitter);
        _hostPanel.Children.Add(_dockerMonSlot);
        _hostPanel.Children.Add(_sysmonSlot);

        if (sftpClient != null)
        {
            _sftpView = new SftpFileBrowserView(sftpClient, client, editors, definition, saveConfig);

            // Wire monitor + tail callbacks before anything fires.
            _sftpView.SysmonToggleRequested    = on => TrySetSysmonEnabled(on);
            _sftpView.DockerMonToggleRequested = on => TrySetDockerMonEnabledAsync(on);
            _sftpView.TailFileRequested        = OpenTailWindow;

            // Restore persisted toggle state from session settings.
            var ss = definition?.Settings as SshSettings;
            _sftpView.SetInitialMonitorState(ss?.SysmonEnabled == true, ss?.DockerMonEnabled == true);
            if (ss?.SysmonEnabled == true)    TrySetSysmonEnabled(true);
            if (ss?.DockerMonEnabled == true) _ = TrySetDockerMonEnabledAsync(true);

            // Navigate to home directory once the SFTP handshake completes.
            // sftpConnectTask may already be completed (synchronous path) or still
            // running (lazy background path); ContinueWith handles both correctly.
            (sftpConnectTask ?? Task.CompletedTask).ContinueWith(_ =>
            {
                if (!sftpClient.IsConnected) return;
                var homeDir = sftpClient.WorkingDirectory;
                Dispatcher.UIThread.Post(() => _sftpView.NavigateTo(homeDir));
            }, TaskScheduler.Default);

            tc.PropertyChanged += (_, args) =>
            {
                if (args.Property == TerminalControl.CurrentDirectoryProperty && args.NewValue is string path)
                    Dispatcher.UIThread.Post(() => _sftpView.OnTerminalDirectoryChanged(path));
            };
        }

        TerminalView.AddTitleChangedHandler(tc, (_, e) =>
        {
            if (!e.Handled) { Title = e.Title; e.Handled = true; }
        });
    }

    /// <summary>
    /// Called from <see cref="SshSessionLauncher.CompleteConnectionAsync"/> once the
    /// <see cref="SshPtyConnection"/> is created. Subscribes to its
    /// <see cref="SshPtyConnection.ConnectionClosed"/> event so the session-ended
    /// overlay is shown when the user exits the shell or the connection drops.
    /// </summary>
    internal void OnPtyConnectionReady(SshPtyConnection connection)
    {
        connection.ConnectionClosed += (_, _) =>
        {
            _connectionEnded = true;
            _sysmonPoller?.Dispose();
            _dockerMonPoller?.Dispose();
            _sftpClient?.Dispose();
            // Close any open tail windows — their underlying exec channels are dead.
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var w in _tailWindows.ToArray())
                {
                    try { w.Close(); } catch { }
                }
                _tailWindows.Clear();
                SessionEnded?.Invoke(this, EventArgs.Empty);
            });
        };
    }

    /// <summary>
    /// Called from <see cref="SshSessionLauncher.CompleteConnectionAsync"/> once the
    /// shell stream is created. Enables bookmark activation to inject <c>cd</c> commands.
    /// </summary>
    internal void SetShellCommand(Action<string> sendCommand)
    {
        _sftpView?.SetShellCommand(sendCommand);
    }

    public Control TabContent => _hostPanel;
    public TerminalControl? Terminal => _tc;
    public Control? SftpPanel => _sftpView;
    public string Title { get; private set; }
    public event EventHandler? SessionEnded;

    // -----------------------------------------------------------------------
    // Sysmon / DockerMon / Tail integration
    // -----------------------------------------------------------------------

    private bool TrySetSysmonEnabled(bool on)
    {
        if (_connectionEnded) return false;
        if (on)
        {
            if (_sysmonPanel == null)
            {
                _sysmonPanel = new SysmonPanel();
                _sysmonSlot.Content = _sysmonPanel;
            }
            if (_sysmonPoller == null)
            {
                _sysmonPoller = new SysmonPoller(_client, TimeSpan.FromSeconds(2));
                _sysmonPoller.SnapshotReceived += (_, snap) => _sysmonPanel?.Update(snap);
            }
            _sysmonSlot.IsVisible = true;
            PersistMonitorState();
            return true;
        }
        else
        {
            _sysmonPoller?.Dispose();
            _sysmonPoller = null;
            _sysmonSlot.IsVisible = false;
            PersistMonitorState();
            return true;
        }
    }

    private async Task<bool> TrySetDockerMonEnabledAsync(bool on)
    {
        if (_connectionEnded) return false;
        if (on)
        {
            // Probe docker first; show an alert and bail if it isn't installed.
            var probe = new DockerMonPoller(_client, TimeSpan.FromSeconds(3));
            var ok = await probe.ProbeAsync();
            if (!ok)
            {
                probe.Dispose();
                var owner = TopLevel.GetTopLevel(_hostPanel) as Window;
                await MessageDialog.ShowAsync(owner, "Docker not available",
                    "Docker is not installed (or not on PATH) on the remote host. " +
                    "DockerMon requires the `docker` CLI.");
                return false;
            }
            _dockerMonPoller = probe;
            _dockerMonPoller.LogsRequested += (_, row) => OpenDockerLogsWindow(row.Id, row.Name);
            _dockerMonPanel = new DockerMonPanel(_dockerMonPoller);
            _dockerMonSlot.Content = _dockerMonPanel;
            _dockerMonRowDef.Height = new GridLength(_dockerMonHeight, GridUnitType.Pixel);
            _dockerSplitter.IsVisible = true;
            _dockerMonSlot.IsVisible = true;
            _dockerMonPoller.Start();
            PersistMonitorState();
            return true;
        }
        else
        {
            _dockerMonPoller?.Dispose();
            _dockerMonPoller = null;
            _dockerMonPanel = null;
            // Save the user-resized height before hiding.
            var h = _dockerMonRowDef.Height;
            if (h.GridUnitType == GridUnitType.Pixel && h.Value > 0)
                _dockerMonHeight = h.Value;
            _dockerMonRowDef.Height = new GridLength(0, GridUnitType.Pixel);
            _dockerSplitter.IsVisible = false;
            _dockerMonSlot.IsVisible = false;
            _dockerMonSlot.Content = null;
            PersistMonitorState();
            return true;
        }
    }

    private void OpenTailWindow(string remotePath)
    {
        if (_connectionEnded || !_client.IsConnected) return;
        var source = new SshTailLogSource(_client, remotePath);
        var window = new LogTailWindow(source, remotePath);
        _tailWindows.Add(window);
        window.Closed += (_, _) => _tailWindows.Remove(window);
        var owner = TopLevel.GetTopLevel(_hostPanel) as Window;
        if (owner != null) window.Show(owner);
        else window.Show();
    }

    private void OpenDockerLogsWindow(string containerId, string name)
    {
        if (_connectionEnded || !_client.IsConnected) return;
        var shortId = containerId.Length > 12 ? containerId[..12] : containerId;
        var label = $"docker:{name} ({shortId})";
        var source = new DockerLogsSource(_client, containerId, label);
        var window = new LogTailWindow(source);
        _tailWindows.Add(window);
        window.Closed += (_, _) => _tailWindows.Remove(window);
        var owner = TopLevel.GetTopLevel(_hostPanel) as Window;
        if (owner != null) window.Show(owner);
        else window.Show();
    }

    private void PersistMonitorState()
    {
        if (_definition?.Settings is not SshSettings ss || _saveConfig == null) return;
        bool sys = _sysmonSlot.IsVisible;
        bool dock = _dockerMonSlot.IsVisible;
        if (ss.SysmonEnabled == sys && ss.DockerMonEnabled == dock) return;
        _definition.Settings = ss with { SysmonEnabled = sys, DockerMonEnabled = dock };
        _saveConfig();
    }

    public void Kill()
    {
        // Stop pollers and close tail windows first so background exec channels
        // don't try to read from a torn-down client.
        try { _sysmonPoller?.Dispose();    } catch { }
        try { _dockerMonPoller?.Dispose(); } catch { }
        foreach (var w in _tailWindows.ToArray())
        {
            try { w.Close(); } catch { }
        }
        _tailWindows.Clear();

        // 1. Disconnect the final-target SSH client — tears down the shell stream.
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _sftpClient?.Dispose(); } catch { }

        // 2. Dispose jump-host chain resources in reverse order.
        if (_chain is not null)
        {
            for (int i = _chain.Forwards.Count - 1; i >= 0; i--)
            {
                try { _chain.Forwards[i].Stop();    } catch { }
                try { _chain.Forwards[i].Dispose(); } catch { }
            }
            for (int i = _chain.JumpClients.Count - 1; i >= 0; i--)
            {
                try { if (_chain.JumpClients[i].IsConnected) _chain.JumpClients[i].Disconnect(); } catch { }
                try { _chain.JumpClients[i].Dispose(); } catch { }
            }
        }

        // 3. Kill the TerminalControl last.
        try { _tc.Kill(); } catch { }
    }

    public void Dispose() => Kill();
}
