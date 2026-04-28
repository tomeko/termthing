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

    public SshSessionLauncher(IKnownHostsService knownHosts, Func<AppConfig> getConfig, EditorRegistry? editors = null)
    {
        _knownHosts = knownHosts;
        _getConfig  = getConfig;
        _editors    = editors;
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
            var instance = new SshSessionInstance(tc, definition.Name, client, sftpClient, sftpConnectTask, chainResult, _editors);
            _ = CompleteConnectionAsync(tc, client, sftpClient, settings, tcs.Task, instance);
            return instance;
        }

        var loadedInstance = new SshSessionInstance(tc, definition.Name, client, sftpClient, sftpConnectTask, chainResult, _editors);
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
    private readonly SshClient _client;
    private readonly SftpClient? _sftpClient;
    private readonly SftpFileBrowserView? _sftpView;
    private readonly SshChainResult? _chain;

    public SshSessionInstance(
        TerminalControl  tc,
        string           title,
        SshClient        client,
        SftpClient?      sftpClient,
        Task?            sftpConnectTask = null,
        SshChainResult?  chain           = null,
        EditorRegistry?  editors         = null)
    {
        _chain = chain;
        _tc = tc;
        _client = client;
        _sftpClient = sftpClient;
        Title = title;

        if (sftpClient != null)
        {
            _sftpView = new SftpFileBrowserView(sftpClient, client, editors);

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
            _sftpClient?.Dispose();
            Dispatcher.UIThread.Post(() => SessionEnded?.Invoke(this, EventArgs.Empty));
        };
    }

    public Control TabContent => _tc;
    public Control? SftpPanel => _sftpView;
    public string Title { get; private set; }
    public event EventHandler? SessionEnded;

    public void Kill()
    {
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
