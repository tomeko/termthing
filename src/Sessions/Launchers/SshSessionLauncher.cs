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

        // Hard guard: the launcher must never re-open the full SSH connect dialog —
        // callers are expected to have collected host/port/username before reaching
        // here. Anything missing is a programmer error and is surfaced loudly so
        // the dialog can never silently re-appear behind subsequent prompts.
        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new InvalidOperationException(
                "SshSettings.Host is required at launch time. The caller must collect connection details before invoking the launcher.");

        // Pre-flight: probe the first hop with a short TCP connect before prompting for
        // credentials, so the user isn't asked for a password/passphrase for an unreachable host.
        var firstHop = settings.JumpHosts.Count > 0 ? settings.JumpHosts[0] : null;
        var probeHost = firstHop is not null ? firstHop.Host : settings.Host;
        var probePort = firstHop is not null ? firstHop.Port : settings.Port;

        // Skip the TCP probe when a ProxyCommand is set and there are no jump hosts —
        // the whole point of ProxyCommand is that the host is NOT reachable over plain
        // TCP from this machine. The proxy process is what knows how to get there.
        bool skipProbe = !string.IsNullOrWhiteSpace(settings.ProxyCommand) && firstHop is null;

        if (!skipProbe && !string.IsNullOrWhiteSpace(probeHost))
        {
            using var probe = new System.Net.Sockets.TcpClient();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await probe.ConnectAsync(probeHost, probePort, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException { CancellationToken.IsCancellationRequested: false })
            {
                var isDnsFailure = ex is System.Net.Sockets.SocketException se
                    && (se.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound
                                           or System.Net.Sockets.SocketError.NoData
                                           or System.Net.Sockets.SocketError.TryAgain)
                    && !System.Net.IPAddress.TryParse(probeHost, out _);

                var detail = isDnsFailure
                    ? $"Cannot reach {probeHost}:{probePort}.\n\nDNS resolution failed — the hostname '{probeHost}' could not be resolved. " +
                      $"Check that the hostname is spelled correctly and that DNS is reachable from this machine.\n\n{ex.Message}"
                    : $"Cannot reach {probeHost}:{probePort}.\n\n{ex.Message}";

                await promptHost.ShowErrorAsync("Host unreachable", detail);
                throw new OperationCanceledException("Host unreachable.");
            }
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

        // Resolve jump-host chain (empty = direct connect).
        // Apply username fallback: if left blank, use the OS login name (matches ssh(1) default).
        var effectiveUsername = string.IsNullOrWhiteSpace(settings.Username)
            ? Environment.UserName
            : settings.Username;

        // For password-based auth (no key file): prompt for a password now, before connecting.
        // We never persist passwords; they live only in the transient field for this session.
        if (string.IsNullOrWhiteSpace(settings.KeyFilePath) &&
            string.IsNullOrEmpty(settings.TransientPassword))
        {
            var password = await promptHost.PromptForPasswordAsync(effectiveUsername, settings.Host);
            if (password is null)
                throw new OperationCanceledException("User cancelled password entry.");
            settings.TransientPassword = password;
        }

        var hops = JumpHostResolver.Resolve(settings, _getConfig());

        // ProxyCommand: spawn an external transport and connect SSH.NET through it
        // via a loopback TCP bridge. v1: not combinable with JumpHosts.
        ProxyCommandTransport? proxyTransport = null;
        if (!string.IsNullOrWhiteSpace(settings.ProxyCommand))
        {
            if (hops.Count > 0)
                throw new NotSupportedException(
                    "Combining ProxyCommand with JumpHosts is not yet supported. Use one or the other.");

            proxyTransport = await ProxyCommandTransport.StartAsync(
                settings.ProxyCommand, settings.Host, settings.Port, effectiveUsername, cancellationToken);
            Debug.WriteLine($"[SSH] ProxyCommand listening on {proxyTransport.LoopbackEndpoint}: {proxyTransport.ResolvedCommand}");
        }

        SshClient client;
        SshChainResult? chainResult = null;

        if (hops.Count == 0)
        {
            // --- Direct connection (existing path) ---
            // When ProxyCommand is in use, point SSH.NET at the loopback bridge but
            // keep the known-hosts lookup keyed by the real host (done in the
            // HostKeyReceived handler below).
            var connectHost = proxyTransport?.LoopbackEndpoint.Address.ToString() ?? settings.Host;
            var connectPort = proxyTransport?.LoopbackEndpoint.Port ?? settings.Port;
            var connectionInfo = SshConnectionInfoFactory.Build(
                connectHost, connectPort, effectiveUsername,
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
            Exception? firstConnectError = null;
            await Task.Run(() =>
            {
                try { client.Connect(); }
                catch (Exception ex) { firstConnectError = ex; }
            }, cancellationToken);
            Debug.WriteLine($"[SSH] Connect: {connectSw.ElapsedMilliseconds}ms  kex={connectionInfo.CurrentKeyExchangeAlgorithm}  cipher={connectionInfo.CurrentServerEncryption}");

            // Only swallow the connect failure when it was caused by an untrusted host
            // key (so we can show the prompt and retry). Anything else — auth failure,
            // bad passphrase, network error — must surface so the user sees what's wrong
            // instead of getting a half-initialised, blank terminal tab.
            if (firstConnectError is not null && pendingKeyArgs is null)
            {
                client.Dispose();
                if (proxyTransport is not null) { await proxyTransport.DisposeAsync(); proxyTransport = null; }
                Debug.WriteLine($"[SSH] Connect failed: {firstConnectError.GetType().Name}: {firstConnectError.Message}");
                throw EnrichAuthException(firstConnectError, settings);
            }

            if (pendingKeyArgs is not null)
            {
                var action = await promptHost.PromptHostKeyAsync(settings.Host, settings.Port,
                    pendingStatus!.Value, pendingKeyArgs);

                if (action == HostKeyAction.Cancel)
                {
                    client.Dispose();
                    if (proxyTransport is not null) { await proxyTransport.DisposeAsync(); proxyTransport = null; }
                    throw new OperationCanceledException("SSH connection aborted by user.");
                }
                if (action == HostKeyAction.TrustAndConnect)
                    _knownHosts.Trust(settings.Host, settings.Port, pendingKeyArgs);

                client.Dispose();

                // ProxyCommand bridges accept a single socket and stop listening — the
                // first connect attempt consumed it, so the retry needs a fresh transport
                // (and a rebuilt ConnectionInfo pointing at the new loopback port).
                if (proxyTransport is not null)
                {
                    await proxyTransport.DisposeAsync();
                    proxyTransport = await ProxyCommandTransport.StartAsync(
                        settings.ProxyCommand!, settings.Host, settings.Port, effectiveUsername, cancellationToken);
                    Debug.WriteLine($"[SSH] ProxyCommand restarted on {proxyTransport.LoopbackEndpoint} for host-key retry.");
                    connectionInfo = SshConnectionInfoFactory.Build(
                        proxyTransport.LoopbackEndpoint.Address.ToString(),
                        proxyTransport.LoopbackEndpoint.Port,
                        effectiveUsername,
                        settings.KeyFilePath, settings.TransientKeyPassphrase, settings.TransientPassword);
                }

                client = new SshClient(connectionInfo);
                client.HostKeyReceived += (_, e) => e.CanTrust = true;
                try
                {
                    await Task.Run(() => client.Connect(), cancellationToken);
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    if (proxyTransport is not null) { await proxyTransport.DisposeAsync(); proxyTransport = null; }
                    Debug.WriteLine($"[SSH] Retry connect after host-key trust failed: {ex.GetType().Name}: {ex.Message}");
                    throw EnrichAuthException(ex, settings);
                }
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

        // Send SSH-level keepalives so that silent TCP drops (power-off, firewall
        // expiry, NAT table timeout) are detected within ~30 s rather than waiting
        // for the OS TCP keepalive timer (default: 2 hours).
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // ----------------------------------------------------------------
        // SFTP — needs a separate client through its own forward (or direct).
        // Built lazily via a connector so the SFTP browser can be opened on
        // demand after connecting (not only at connect time). The connector
        // captures everything needed to stand up an independent SFTP channel.
        // ----------------------------------------------------------------
        var chainForSftp = chainResult; // capture (may be null for direct)
        Func<CancellationToken, Task<SftpConnection>> sftpConnector = async ct2 =>
        {
            SftpClient sftpClient;
            ProxyCommandTransport? localSftpProxy = null;
            Renci.SshNet.ForwardedPortLocal? sftpFwd = null;
            var sftpSw = Stopwatch.StartNew();

            if (chainForSftp is null)
            {
                // Direct — reuse the same ConnectionInfo as the shell client.
                // When ProxyCommand is in use, start a second transport so SFTP has
                // its own independent process+socket (the shell transport's listener
                // has already accepted its one connection).
                string sftpHost = settings.Host;
                int    sftpPort = settings.Port;
                if (proxyTransport is not null)
                {
                    localSftpProxy = await ProxyCommandTransport.StartAsync(
                        settings.ProxyCommand!, settings.Host, settings.Port, effectiveUsername, ct2);
                    sftpHost = localSftpProxy.LoopbackEndpoint.Address.ToString();
                    sftpPort = localSftpProxy.LoopbackEndpoint.Port;
                }
                var directCi = SshConnectionInfoFactory.Build(
                    sftpHost, sftpPort, effectiveUsername,
                    settings.KeyFilePath, settings.TransientKeyPassphrase, settings.TransientPassword);
                sftpClient = new SftpClient(directCi);
            }
            else
            {
                // Tunnelled — open a new forward on the last jump client so SFTP
                // gets its own independent TCP stream.
                var lastJump = chainForSftp.JumpClients.Count > 0
                    ? chainForSftp.JumpClients[^1]
                    : null;
                if (lastJump is not null)
                {
                    sftpFwd = new Renci.SshNet.ForwardedPortLocal(
                        System.Net.IPAddress.Loopback.ToString(), (uint)0,
                        settings.Host, (uint)settings.Port);
                    lastJump.AddForwardedPort(sftpFwd);
                    sftpFwd.Start();
                    var sftpCi = SshConnectionInfoFactory.Build(
                        System.Net.IPAddress.Loopback.ToString(), (int)sftpFwd.BoundPort,
                        effectiveUsername, settings.KeyFilePath,
                        settings.TransientKeyPassphrase, settings.TransientPassword);
                    sftpClient = new SftpClient(sftpCi);
                }
                else
                {
                    // Edge case: chain resolved but no jump clients (shouldn't happen).
                    sftpClient = new SftpClient(chainForSftp.FinalConnectionInfo);
                }
            }

            var connectTask = Task.Run(() => sftpClient.Connect(), ct2)
                .ContinueWith(t =>
                {
                    Debug.WriteLine($"[SSH] SFTP connect: {sftpSw.ElapsedMilliseconds}ms");
                    return t;
                }, TaskScheduler.Default).Unwrap();

            return new SftpConnection(sftpClient, connectTask, localSftpProxy, sftpFwd);
        };

        // Auto-open SFTP at connect only when requested AND initialization is not
        // deferred. When deferred, the user opens SFTP manually later (a point at
        // which the session is known to be past any in-shell prompt), and the
        // shell-integration hook is injected only at that point.
        bool autoOpenSftp = settings.EnableSftp && !settings.DeferInitialization;
        SftpConnection? eagerSftp = autoOpenSftp
            ? await sftpConnector(cancellationToken)
            : null;

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

        TerminalContextMenuBehavior.Attach(tc, definition, _saveConfig);

        if (!tc.IsLoaded)
        {
            var tcs = new TaskCompletionSource<bool>();
            tc.Loaded += (_, _) => tcs.TrySetResult(true);
            var instance = new SshSessionInstance(tc, definition.Name, client, eagerSftp, sftpConnector, chainResult, _editors, definition, _saveConfig, proxyTransport);
            // Fire-and-forget by necessity (we have to return the instance before the
            // control is loaded), but observe the task so failures aren't silent —
            // they would otherwise leave a blank terminal that can't accept input.
            _ = CompleteConnectionAsync(tc, client, settings, tcs.Task, instance)
                .ContinueWith(t =>
                {
                    if (t.Exception is { } ex)
                    {
                        var inner = ex.GetBaseException();
                        Debug.WriteLine($"[SSH] CompleteConnectionAsync failed: {inner.GetType().Name}: {inner.Message}");
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                var msg = $"\r\n\u001b[1;31mSSH session failed: {inner.GetType().Name}: {inner.Message}\u001b[0m\r\n";
                                tc.Terminal?.Write(msg);
                            }
                            catch { }
                        });
                    }
                }, TaskScheduler.Default);
            return instance;
        }

        var loadedInstance = new SshSessionInstance(tc, definition.Name, client, eagerSftp, sftpConnector, chainResult, _editors, definition, _saveConfig, proxyTransport);
        await CompleteConnectionAsync(tc, client, settings, Task.CompletedTask, loadedInstance);
        return loadedInstance;
    }

    private static async Task CompleteConnectionAsync(
        TerminalControl tc,
        SshClient client,
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
        // Ctrl+U (0x15) kills the current input line before injecting the command,
        // so a partially-typed user command can never interleave with the cd.
        instance.SetShellCommand(line => { shell.Write("\x15"); shell.WriteLine(line); });
        var connection = new SshPtyConnection(client, shell);
        instance.OnPtyConnectionReady(connection);

        // Shell-integration (OSC 7 directory tracking) is injected by the instance
        // when the SFTP browser is active — see SshSessionInstance.EnableShellIntegration.
        // Doing it there (rather than unconditionally here) means it is never injected
        // into an in-shell prompt that appears before the SFTP browser is opened,
        // which is the whole point of the "Defer initialization" option.
        instance.MarkReadyForShellIntegration();

        await tc.AttachConnection(connection);
    }

    /// <summary>
    /// Wrap an SSH.NET authentication failure with a clearer, actionable message.
    /// SSH.NET's <see cref="SshAuthenticationException"/> for a public-key auth
    /// rejection is just <c>"Permission denied (publickey)."</c>, which is ambiguous —
    /// it can mean the passphrase decrypted to the wrong key, the key isn't in the
    /// server's <c>authorized_keys</c>, or the username is wrong. This rewrite
    /// surfaces those possibilities in the error dialog so the user has somewhere
    /// to look first.
    /// </summary>
    private static Exception EnrichAuthException(Exception ex, SshSettings settings)
    {
        if (ex is not SshAuthenticationException auth)
            return ex;

        var hasKey      = !string.IsNullOrWhiteSpace(settings.KeyFilePath);
        var hasPassword = !string.IsNullOrEmpty(settings.TransientPassword);
        var detail = (hasKey, hasPassword) switch
        {
            (true,  false) =>
                $"{auth.Message}\n\nThe server rejected your private key for user '{settings.Username}'. " +
                "Common causes:\n" +
                "  • The matching public key is not in ~/.ssh/authorized_keys on the server\n" +
                "  • The wrong username (verify with: ssh -v -i <key> user@host)\n" +
                "  • The key file path points at a different key than you expect\n\n" +
                "Tip: 'ssh-copy-id -i <key>.pub user@host' adds the key to the server.",
            (false, true) =>
                $"{auth.Message}\n\nThe server rejected the password for user '{settings.Username}'.",
            (true,  true) =>
                $"{auth.Message}\n\nNeither the key nor the password were accepted for user '{settings.Username}'.",
            _ =>
                $"{auth.Message}\n\nNo credentials were supplied for user '{settings.Username}'.",
        };
        return new SshAuthenticationException(detail);
    }

}

/// <summary>
/// A live SFTP channel plus the disposable resources that back it. Built by the
/// launcher's SFTP connector, either eagerly at connect or lazily when the user
/// opens the SFTP browser after connecting.
/// </summary>
internal sealed record SftpConnection(
    SftpClient Client,
    Task ConnectTask,
    IAsyncDisposable? ProxyTransport,
    Renci.SshNet.ForwardedPortLocal? Forward);

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

    // SFTP is opened lazily: _sftpConnector stands up a fresh channel on demand,
    // _sftpConnection/_sftpClient/_sftpView are null until SFTP is actually open.
    private readonly Func<CancellationToken, Task<SftpConnection>>? _sftpConnector;
    private SftpConnection? _sftpConnection;
    private SftpClient? _sftpClient;
    private SftpFileBrowserView? _sftpView;
    private readonly EditorRegistry? _editors;
    private Action<string>? _shellCommand;
    private bool _readyForShellIntegration;
    private bool _shellIntegrationInjected;
    private bool _togglingSftp;

    private readonly SshChainResult? _chain;
    private readonly SessionDefinition? _definition;
    private readonly Action? _saveConfig;
    private readonly IAsyncDisposable? _proxyTransport;

    private SysmonPoller? _sysmonPoller;
    private SysmonPanel? _sysmonPanel;
    private DockerMonPoller? _dockerMonPoller;
    private DockerMonPanel? _dockerMonPanel;

    private readonly List<LogTailWindow> _tailWindows = new();
    private bool _connectionEnded;
    private int _sessionEndedFired; // Interlocked guard — ensures SessionEnded fires at most once

    public SshSessionInstance(
        TerminalControl  tc,
        string           title,
        SshClient        client,
        SftpConnection?  eagerSftp,
        Func<CancellationToken, Task<SftpConnection>>? sftpConnector = null,
        SshChainResult?  chain               = null,
        EditorRegistry?  editors             = null,
        SessionDefinition? definition        = null,
        Action?          saveConfig          = null,
        IAsyncDisposable? proxyTransport     = null)
    {
        _chain = chain;
        _tc = tc;
        _client = client;
        _sftpConnector = sftpConnector;
        _editors = editors;
        _definition = definition;
        _saveConfig = saveConfig;
        _proxyTransport = proxyTransport;
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

        // Propagate terminal directory changes to the SFTP view whenever one is open.
        tc.PropertyChanged += (_, args) =>
        {
            if (args.Property == TerminalControl.CurrentDirectoryProperty && args.NewValue is string path)
                Dispatcher.UIThread.Post(() => _sftpView?.OnTerminalDirectoryChanged(path));
        };

        // Eager open (SFTP requested at connect and initialization not deferred).
        if (eagerSftp is not null)
            AttachSftp(eagerSftp);

        TerminalView.AddTitleChangedHandler(tc, (_, e) =>
        {
            if (!e.Handled) { Title = e.Title; e.Handled = true; }
        });
    }

    /// <summary>
    /// Wires a freshly-connected <see cref="SftpConnection"/> into a new
    /// <see cref="SftpFileBrowserView"/>, restores monitor toggles, and navigates to
    /// the remote home directory once the handshake completes. Shared by the eager
    /// (connect-time) path and the lazy <see cref="SetSftpEnabledAsync"/> path.
    /// </summary>
    private void AttachSftp(SftpConnection conn)
    {
        _sftpConnection = conn;
        _sftpClient     = conn.Client;

        var view = new SftpFileBrowserView(conn.Client, _client, _editors, _definition, _saveConfig)
        {
            SysmonToggleRequested    = on => TrySetSysmonEnabled(on),
            DockerMonToggleRequested = on => TrySetDockerMonEnabledAsync(on),
            TailFileRequested        = OpenTailWindow,
        };
        _sftpView = view;

        view.CloseRequested += (_, _) => _ = SetSftpEnabledAsync(false);

        if (_shellCommand is not null)
            view.SetShellCommand(_shellCommand);

        // Restore persisted monitor toggle state from session settings.
        var ss = _definition?.Settings as SshSettings;
        view.SetInitialMonitorState(ss?.SysmonEnabled == true, ss?.DockerMonEnabled == true);
        if (ss?.SysmonEnabled == true)    TrySetSysmonEnabled(true);
        if (ss?.DockerMonEnabled == true) _ = TrySetDockerMonEnabledAsync(true);

        // Opening SFTP is a known-safe point to enable OSC 7 directory tracking:
        // the user has explicitly asked for the browser, so the shell is past any
        // in-session password/passphrase prompt.
        EnableShellIntegration();

        // Navigate to the remote home directory once the SFTP handshake completes.
        conn.ConnectTask.ContinueWith(_ =>
        {
            if (!conn.Client.IsConnected) return;
            var homeDir = conn.Client.WorkingDirectory;
            Dispatcher.UIThread.Post(() => view.NavigateTo(homeDir));
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// The OSC 7 shell-integration hook, written to run under whichever shell the
    /// remote account happens to use.
    /// <para>
    /// Three things make this fiddly. <c>PROMPT_COMMAND</c> is a bash feature — zsh
    /// ignores it entirely and reports the working directory from
    /// <c>precmd_functions</c> instead. zsh also leaves <c>INTERACTIVE_COMMENTS</c>
    /// off by default, so a trailing <c>#</c> comment is not a comment at all and the
    /// shell answers with <c>command not found: #</c>; the sentinel therefore rides on
    /// a variable assignment, which every shell accepts. And the zsh array-append
    /// syntax has to sit inside <c>eval</c>, because a plain <c>sh</c> parses the whole
    /// line before running it and would reject <c>+=(...)</c> even in the branch it
    /// never takes.
    /// </para>
    /// <para>
    /// The bash branch prepends rather than assigns, so an existing
    /// <c>PROMPT_COMMAND</c> from the user's own profile keeps working.
    /// </para>
    /// </summary>
    private const string Osc7ShellIntegrationCommand =
        "__ICTERMINT__=1; " +
        "__ictermint_cwd() { printf '\\033]7;file://%s%s\\007' \"${HOSTNAME:-${HOST:-}}\" \"$PWD\"; }; " +
        "if [ -n \"${ZSH_VERSION:-}\" ]; then " +
            "eval 'typeset -ga precmd_functions; precmd_functions+=(__ictermint_cwd)'; " +
        "elif [ -n \"${BASH_VERSION:-}\" ]; then " +
            "PROMPT_COMMAND=\"__ictermint_cwd${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"; " +
        "fi";

    /// <summary>
    /// Injects the OSC 7 shell-integration hook (once) so the terminal reports its
    /// working directory. Only fires after the shell stream is ready
    /// (<see cref="MarkReadyForShellIntegration"/>) and when OSC 7 is enabled for this
    /// session. The terminal sends it on the next chunk of PTY output.
    /// </summary>
    private void EnableShellIntegration()
    {
        if (_shellIntegrationInjected || !_readyForShellIntegration) return;
        if (_definition?.Settings is SshSettings ss && !ss.ShellIntegrationOsc7) return;
        _shellIntegrationInjected = true;
        Dispatcher.UIThread.Post(() => _tc.ShellIntegrationCommand = Osc7ShellIntegrationCommand);
    }

    /// <summary>
    /// Called by the launcher once the PTY shell stream is attached. Marks the
    /// session as ready for shell-integration injection and, if SFTP is already
    /// open, injects immediately.
    /// </summary>
    internal void MarkReadyForShellIntegration()
    {
        _readyForShellIntegration = true;
        if (_sftpView is not null)
            EnableShellIntegration();
    }

    // -----------------------------------------------------------------------
    // Post-connect SFTP open/close
    // -----------------------------------------------------------------------

    public bool CanUseSftp => _sftpConnector is not null;
    public bool IsSftpActive => _sftpView is not null;
    public event EventHandler? SftpPanelChanged;

    /// <summary>
    /// Opens (<paramref name="on"/> = true) or closes the SFTP browser after the
    /// session has connected. Idempotent; safe to call from the UI thread.
    /// </summary>
    public async Task<bool> SetSftpEnabledAsync(bool on)
    {
        if (_connectionEnded) return false;
        if (_togglingSftp) return false;
        _togglingSftp = true;
        try
        {
            if (on)
            {
                if (_sftpView is not null) return true;      // already open
                if (_sftpConnector is null) return false;

                SftpConnection conn;
                try { conn = await _sftpConnector(CancellationToken.None); }
                catch { return false; }

                AttachSftp(conn);
                SftpPanelChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            else
            {
                if (_sftpView is null) return true;          // already closed
                _sftpView = null;
                var conn = _sftpConnection;
                _sftpConnection = null;
                _sftpClient = null;
                await DisposeSftpConnectionAsync(conn);
                SftpPanelChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }
        finally
        {
            _togglingSftp = false;
        }
    }

    private static async Task DisposeSftpConnectionAsync(SftpConnection? conn)
    {
        if (conn is null) return;
        try { conn.Client.Dispose(); } catch { }
        if (conn.Forward is not null)
        {
            try { conn.Forward.Stop();    } catch { }
            try { conn.Forward.Dispose(); } catch { }
        }
        if (conn.ProxyTransport is not null)
        {
            try { await conn.ProxyTransport.DisposeAsync(); } catch { }
        }
    }

    /// <summary>
    /// Called from <see cref="SshSessionLauncher.CompleteConnectionAsync"/> once the
    /// <see cref="SshPtyConnection"/> is created. Subscribes to its
    /// <see cref="SshPtyConnection.ConnectionClosed"/> event so the session-ended
    /// overlay is shown when the user exits the shell or the connection drops.
    /// </summary>
    internal void OnPtyConnectionReady(SshPtyConnection connection)
    {
        // Both ConnectionClosed (SSH.NET detected the drop) and ProcessExited
        // (TerminalView EOF fallback) route here so the overlay always appears.
        void FireSessionEnded()
        {
            if (Interlocked.CompareExchange(ref _sessionEndedFired, 1, 0) != 0) return;
            _connectionEnded = true;
            _sysmonPoller?.Dispose();
            _dockerMonPoller?.Dispose();
            _ = DisposeSftpConnectionAsync(_sftpConnection);
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
        }

        connection.ConnectionClosed += (_, _) => FireSessionEnded();

        // Fallback: TerminalView raises ProcessExited when ReadAsync returns 0
        // (EOF on the shell stream). This fires even if ConnectionClosed was missed,
        // e.g. if the shell closed cleanly but ErrorOccurred was never raised.
        _tc.ProcessExited += (_, _) => FireSessionEnded();
    }

    /// <summary>
    /// Called from <see cref="SshSessionLauncher.CompleteConnectionAsync"/> once the
    /// shell stream is created. Enables bookmark activation to inject <c>cd</c> commands.
    /// </summary>
    internal void SetShellCommand(Action<string> sendCommand)
    {
        _shellCommand = sendCommand;
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
        // SFTP client + its forward + its ProxyCommand transport (if any).
        _ = DisposeSftpConnectionAsync(_sftpConnection);

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

        // 3. The shell's ProxyCommand transport — fire-and-forget; the bridge's
        //    DisposeAsync kills the spawned process and stops the loopback listener.
        //    Idempotent and safe to drop on the floor here. (The SFTP transport is
        //    disposed above via DisposeSftpConnectionAsync.)
        if (_proxyTransport is not null)
            _ = _proxyTransport.DisposeAsync().AsTask();

        // 4. Kill the TerminalControl last.
        try { _tc.Kill(); } catch { }
    }

    public void Dispose() => Kill();
}
