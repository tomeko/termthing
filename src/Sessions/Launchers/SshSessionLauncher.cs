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
using TermThing.Panes;
using TermThing.Ssh;
using TermThing.Tmux;
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

    public Task<ISessionInstance> LaunchAsync(
        SessionDefinition definition,
        ISessionPromptHost promptHost,
        CancellationToken cancellationToken = default) =>
        LaunchAsync(definition, promptHost, tmuxControlSession: null, fallbackToShell: true, cancellationToken);

    /// <summary>
    /// Connects like <see cref="LaunchAsync(SessionDefinition, ISessionPromptHost, CancellationToken)"/>.
    /// With <paramref name="tmuxControlSession"/>, the tab shows that tmux session through
    /// control mode instead of opening a shell (the session is created if it doesn't exist).
    /// Without one, the session's tmux auto-attach is opened that way when the host's tmux
    /// supports control mode.
    /// </summary>
    /// <param name="fallbackToShell">
    /// When control mode can't start: open a shell and say why (true), or fail the launch (false).
    /// </param>
    public async Task<ISessionInstance> LaunchAsync(
        SessionDefinition definition,
        ISessionPromptHost promptHost,
        string? tmuxControlSession,
        bool fallbackToShell,
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

        // tmux auto-attach: through control mode when the host's tmux is new enough. Older
        // hosts get the legacy attach typed into the shell (MainWindow.StartTmux).
        string? tmuxVersion = null;
        bool probed = false;
        if (string.IsNullOrEmpty(tmuxControlSession) && !string.IsNullOrWhiteSpace(settings.TmuxAutoAttach))
        {
            tmuxVersion = await Task.Run(() => TmuxClient.Probe(client), cancellationToken);
            probed = true;
            if (TmuxClient.SupportsControlMode(tmuxVersion))
                tmuxControlSession = settings.TmuxAutoAttach.Trim();
        }

        if (!string.IsNullOrEmpty(tmuxControlSession))
        {
            var tmuxInstance = new SshSessionInstance(null, definition.Name, client, eagerSftp, sftpConnector, chainResult,
                _editors, definition, _saveConfig, proxyTransport, tmuxControlSession)
            {
                TmuxAutoAttachHandled = true,
            };
            if (probed) tmuxInstance.SetTmuxProbe(tmuxVersion);
            await tmuxInstance.StartTmuxControlAsync(fallbackToShell);
            return tmuxInstance;
        }

        // ----------------------------------------------------------------
        // Build terminal control and launch.
        // ----------------------------------------------------------------
        var tc = TerminalFactory.Create(definition, _saveConfig);
        tc.Process = string.Empty;

        if (!tc.IsLoaded)
        {
            var tcs = new TaskCompletionSource<bool>();
            tc.Loaded += (_, _) => tcs.TrySetResult(true);
            var instance = new SshSessionInstance(tc, definition.Name, client, eagerSftp, sftpConnector, chainResult, _editors, definition, _saveConfig, proxyTransport);
            if (probed) instance.SetTmuxProbe(tmuxVersion);
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
        if (probed) loadedInstance.SetTmuxProbe(tmuxVersion);
        await CompleteConnectionAsync(tc, client, settings, Task.CompletedTask, loadedInstance);
        return loadedInstance;
    }

    /// <summary>
    /// Opens the shell channel for a tab's first pane once its terminal has loaded, and
    /// attaches it. Also used when a tab that opened straight into tmux leaves it.
    /// </summary>
    internal static async Task CompleteConnectionAsync(
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
        // The session instance owns the client (SshSessionInstance.Kill disconnects it), so
        // the first pane can be closed while split panes keep using the connection.
        var connection = new SshPtyConnection(client, shell) { OwnsClient = false };
        instance.OnPtyConnectionReady(connection);

        // Shell-integration (OSC 7 directory tracking) is injected by the instance
        // when the SFTP browser is active — see SshSessionInstance.EnableShellIntegration.
        // Doing it there (rather than unconditionally here) means it is never injected
        // into an in-shell prompt that appears before the SFTP browser is opened,
        // which is the whole point of the "Defer initialization" option.
        instance.MarkReadyForShellIntegration();

        tc.AttachConnection(connection);

        // Claim focus once the input system has settled, as LaunchProcess does for local shells.
        Dispatcher.UIThread.Post(() =>
        {
            if (!tc.IsFocused)
                tc.Focus();
        }, DispatcherPriority.Input);
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
    private TerminalControl? _tc;   // the shell's first pane; null until the tab has a shell
    private readonly Grid _hostPanel;
    private readonly Panel _contentHost;   // the shell's panes, and the tmux view over them in tmux mode
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

    private readonly Dictionary<string, LogTailWindow> _tailWindows = new(); // keyed by source ("file:<path>", "docker:<id>")
    private bool _connectionEnded;
    private int _sessionEndedFired; // Interlocked guard — ensures SessionEnded fires at most once
    private SshPtyConnection? _connection; // the first pane's; owned by us: an attached connection is never disposed by the terminal

    // Split panes: each pane is its own shell channel on _client. Keyed by the pane's
    // terminal; holds the connection (ours to dispose) and a way to type into the shell.
    private PaneLayoutView? _panes;   // null until the tab has a shell
    private readonly PaneTitles _titles;
    private sealed class PaneShell
    {
        public SshPtyConnection? Connection;
        public Action<string>? Send;
    }
    private readonly Dictionary<TerminalControl, PaneShell> _paneShells = new();

    // tmux: whether it's installed (null = not probed yet), the client our shell is
    // running (refreshed by a slow poll), and a reattach waiting for the shell.
    private static readonly TimeSpan TmuxPollInterval = TimeSpan.FromSeconds(5);
    private Timer? _tmuxTimer;
    private int _tmuxPolling;
    private volatile string? _tmuxVersion;
    private bool _tmuxProbed;
    private volatile TmuxOwnClient? _tmuxOwn;
    private string? _pendingTmuxAttach;

    // tmux mode: the tab shows a tmux session's windows and panes natively through
    // control mode, over the shell (kept running, hidden) if the tab has one. A tab
    // opened straight into tmux has no shell until it leaves tmux.
    private TmuxControlSession? _tmuxControl;
    private bool _tmuxExitExpected;   // the user killed the session: no notice when tmux ends the client
    private readonly List<string> _pendingNotices = new();
    private Action<string>? _notice;

    /// <param name="tc">The first pane's terminal; null to open straight into tmux mode.</param>
    /// <param name="tmuxControlSession">With a null <paramref name="tc"/>: the tmux session to show.</param>
    public SshSessionInstance(
        TerminalControl? tc,
        string           title,
        SshClient        client,
        SftpConnection?  eagerSftp,
        Func<CancellationToken, Task<SftpConnection>>? sftpConnector = null,
        SshChainResult?  chain               = null,
        EditorRegistry?  editors             = null,
        SessionDefinition? definition        = null,
        Action?          saveConfig          = null,
        IAsyncDisposable? proxyTransport     = null,
        string?          tmuxControlSession  = null)
    {
        _chain = chain;
        _tc = tc;
        _client = client;
        _sftpConnector = sftpConnector;
        _editors = editors;
        _definition = definition;
        _saveConfig = saveConfig;
        _proxyTransport = proxyTransport;
        _titles = new PaneTitles(title, () => Terminal);
        _titles.Changed += (_, _) => TitleChanged?.Invoke(this, EventArgs.Empty);

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

        _contentHost = new Panel();
        if (tc is not null)
            InitShell(tc);
        else
            CreateTmuxControl(tmuxControlSession ?? throw new ArgumentNullException(nameof(tmuxControlSession)));

        Grid.SetRow(_contentHost, 0);
        Grid.SetRow(_dockerSplitter, 1);
        Grid.SetRow(_dockerMonSlot, 2);
        Grid.SetRow(_sysmonSlot, 3);
        _hostPanel.Children.Add(_contentHost);
        _hostPanel.Children.Add(_dockerSplitter);
        _hostPanel.Children.Add(_dockerMonSlot);
        _hostPanel.Children.Add(_sysmonSlot);

        // Eager open (SFTP requested at connect and initialization not deferred).
        if (eagerSftp is not null)
            AttachSftp(eagerSftp);
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

        view.SetShellCommand(SendToActiveShell);

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
        foreach (var pane in _paneShells.Values)
            pane.Connection?.ArmShellIntegration(Osc7ShellIntegrationCommand);
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

        if (_pendingTmuxAttach is { } session)
        {
            _pendingTmuxAttach = null;
            _connection?.SendWhenIdle(TmuxClient.AttachCommand(session));
        }
        _tmuxTimer ??= new Timer(_ => PollTmux(), null, TimeSpan.FromSeconds(2), TmuxPollInterval);
    }

    // -----------------------------------------------------------------------
    // tmux
    // -----------------------------------------------------------------------

    /// <summary>
    /// Raised (UI thread) when what the tab's TMUX badge shows may have changed: tmux
    /// mode entered or left, a legacy client appeared or went in the shell, or the probe
    /// found tmux missing.
    /// </summary>
    public event EventHandler? TmuxStateChanged;

    /// <summary>
    /// Raised (UI thread) when the tab switches between its shell and tmux mode, or gets
    /// a shell. <see cref="Panes"/>, <see cref="Terminal"/> and <see cref="Terminals"/> change.
    /// </summary>
    public event EventHandler? ModeChanged;

    /// <summary>
    /// Something to tell the user (UI thread), e.g. why tmux mode ended. Notices raised
    /// before anyone listens are delivered to the first handler.
    /// </summary>
    public event Action<string>? Notice
    {
        add
        {
            _notice += value;
            foreach (var message in _pendingNotices) value?.Invoke(message);
            _pendingNotices.Clear();
        }
        remove => _notice -= value;
    }

    private void RaiseNotice(string message)
    {
        if (_notice is { } handler) handler(message);
        else _pendingNotices.Add(message);
    }

    private void RaiseTmuxStateChanged() =>
        Dispatcher.UIThread.Post(() => TmuxStateChanged?.Invoke(this, EventArgs.Empty));

    /// <summary>The installed tmux's version line (e.g. "tmux 3.4"), once probed; null when missing or not probed yet.</summary>
    public string? TmuxVersion => _tmuxVersion;

    /// <summary>What the TMUX badge shows for this tab.</summary>
    public TmuxBadgeState TmuxBadgeState =>
        _tmuxControl is not null ? TmuxBadgeState.Control
        : _tmuxOwn is not null ? TmuxBadgeState.Legacy
        : _tmuxProbed && _tmuxVersion is null ? TmuxBadgeState.Unavailable
        : TmuxBadgeState.Off;

    /// <summary>True while the tab shows a tmux session through control mode.</summary>
    public bool IsTmuxMode => _tmuxControl is not null;

    /// <summary>
    /// Set by the launcher when it has dealt with the session's tmux auto-attach itself
    /// (control mode); the legacy typed attach must not run on top.
    /// </summary>
    internal bool TmuxAutoAttachHandled { get; set; }

    /// <summary>Records a probe the launcher has already run, so it isn't run again.</summary>
    internal void SetTmuxProbe(string? version)
    {
        _tmuxProbed = true;
        _tmuxVersion = version;
    }

    /// <summary>
    /// The tmux session this tab's shell was last seen attached to (legacy), or null.
    /// Kept after the connection drops so a reconnect can reattach it.
    /// </summary>
    public string? TmuxSession => _tmuxOwn?.SessionName;

    private void SetTmuxOwn(TmuxOwnClient? own)
    {
        var before = _tmuxOwn?.SessionName;
        _tmuxOwn = own;
        if (before != own?.SessionName) RaiseTmuxStateChanged();
    }

    /// <summary>
    /// Background poll: probes for tmux once, then tracks which session (if any) the
    /// shell's tmux client shows. Stops for good when tmux isn't installed.
    /// </summary>
    private void PollTmux()
    {
        if (_connectionEnded || Interlocked.Exchange(ref _tmuxPolling, 1) == 1) return;
        try
        {
            if (!_client.IsConnected) return;
            if (!_tmuxProbed)
            {
                _tmuxProbed = true;
                _tmuxVersion = TmuxClient.Probe(_client);
                if (_tmuxVersion is null) { _tmuxTimer?.Dispose(); RaiseTmuxStateChanged(); return; }
            }
            else if (_tmuxVersion is null) { _tmuxTimer?.Dispose(); return; }
            var own = TmuxClient.FindOwnClient(_client);
            // Keep the last known session across a poll that ran into the drop itself.
            if (!_connectionEnded && _client.IsConnected) SetTmuxOwn(own);
        }
        catch { /* connection going away; the next poll or SessionEnded settles it */ }
        finally { Interlocked.Exchange(ref _tmuxPolling, 0); }
    }

    /// <summary>Snapshot for the tab's tmux menu. Null version means tmux isn't available.</summary>
    internal sealed record TmuxMenuState(string? Version, IReadOnlyList<TmuxSessionInfo> Sessions, TmuxOwnClient? Own)
    {
        /// <summary>Value identity for the menu's contents (the list itself compares by reference).</summary>
        public string Signature { get; } =
            $"{Version}|{Own?.SessionName}|{string.Join("|", Sessions.Select(s => $"{s.Name}/{s.Windows}/{s.AttachedClients}"))}";
    }

    /// <summary>Result of the last <see cref="GetTmuxMenuStateAsync"/>, shown while a fresh one loads.</summary>
    internal TmuxMenuState? LastTmuxMenuState { get; private set; }

    internal async Task<TmuxMenuState> GetTmuxMenuStateAsync()
    {
        bool inTmux = _tmuxControl is not null;
        var state = await Task.Run(() =>
        {
            if (_connectionEnded || !_client.IsConnected) return new TmuxMenuState(null, [], null);
            try
            {
                if (!_tmuxProbed) { _tmuxProbed = true; _tmuxVersion = TmuxClient.Probe(_client); }
                if (_tmuxVersion is null) return new TmuxMenuState(null, [], null);
                // The shell's own client only matters while the shell is on show.
                var own = inTmux ? _tmuxOwn : TmuxClient.FindOwnClient(_client);
                return new TmuxMenuState(_tmuxVersion, TmuxClient.ListSessions(_client), own);
            }
            catch { return new TmuxMenuState(null, [], null); }
        });
        if (!inTmux && state.Version is not null) SetTmuxOwn(state.Own);
        if (state.Version is null) RaiseTmuxStateChanged();
        return LastTmuxMenuState = state;
    }

    private const string AmbiguousClients =
        "Several of this tab's panes run tmux, and there is no telling which is which. " +
        "Use tmux's own keys (prefix + d to detach) in the pane you mean.";

    /// <summary>
    /// Legacy: shows <paramref name="session"/> in the shell. Inside tmux already, the
    /// client is switched over (no nesting); otherwise the attach command is typed into
    /// the shell. Returns an error message, or null on success.
    /// </summary>
    internal async Task<string?> AttachTmuxAsync(string session)
    {
        if (_connectionEnded) return "Session is disconnected.";
        var owns = await Task.Run(() => TmuxClient.FindOwnClients(_client));
        if (owns.Count > 1) return AmbiguousClients;
        if (owns.Count == 1)
        {
            var own = owns[0];
            if (own.SessionName == session) return null;
            var err = await Task.Run(() => TmuxClient.SwitchClient(_client, own.ClientTty, session));
            if (err is null) SetTmuxOwn(own with { SessionName = session });
            return err;
        }
        if (ShellCommand is not { } send) return "Shell is not ready yet.";
        send(TmuxClient.AttachCommand(session));
        SetTmuxOwn(new TmuxOwnClient(string.Empty, session)); // confirmed by the next poll
        return null;
    }

    /// <summary>Legacy: starts a new session (named, or tmux's choice) and shows it in the shell.</summary>
    internal async Task<string?> NewTmuxSessionAsync(string? name)
    {
        if (_connectionEnded) return "Session is disconnected.";
        var owns = await Task.Run(() => TmuxClient.FindOwnClients(_client));
        if (owns.Count > 1) return AmbiguousClients;
        if (owns.Count == 0)
        {
            if (ShellCommand is not { } send) return "Shell is not ready yet.";
            send(TmuxClient.NewSessionCommand(name));
            return null;
        }
        // Inside tmux: create it detached, then switch this client over.
        string? error = null;
        var created = await Task.Run(() => TmuxClient.NewDetachedSession(_client, name, out error));
        return created is null ? error : await AttachTmuxAsync(created);
    }

    /// <summary>Legacy: detaches the shell's tmux client, returning to the shell underneath.</summary>
    internal async Task<string?> DetachTmuxAsync()
    {
        if (_connectionEnded) return "Session is disconnected.";
        var owns = await Task.Run(() => TmuxClient.FindOwnClients(_client));
        if (owns.Count == 0) return "This tab's shell is not inside tmux.";
        if (owns.Count > 1) return AmbiguousClients;
        var err = await Task.Run(() => TmuxClient.DetachClient(_client, owns[0].ClientTty));
        if (err is null) SetTmuxOwn(null);
        return err;
    }

    /// <summary>
    /// Legacy: reattaches <paramref name="session"/> once the fresh shell is idle at its
    /// prompt. Used after a reconnect, and for auto-attach on hosts whose tmux is too old
    /// for control mode. Skipped for sessions with deferred initialization, whose first
    /// prompt may be a password prompt that must not receive typed input.
    /// </summary>
    internal void AttachTmuxWhenReady(string session)
    {
        if (_definition?.Settings is SshSettings { DeferInitialization: true }) return;
        SetTmuxOwn(new TmuxOwnClient(string.Empty, session));
        if (_readyForShellIntegration && _connection is not null)
            _connection.SendWhenIdle(TmuxClient.AttachCommand(session));
        else
            _pendingTmuxAttach = session;
    }

    /// <summary>The tmux session this tab shows through control mode, or null when it shows its shell.</summary>
    public string? TmuxControlSession => _tmuxControl?.SessionName;

    /// <summary>Why the session ended, when there is something to tell (e.g. tmux's exit reason).</summary>
    public string? EndMessage { get; private set; }

    /// <summary>
    /// Launch path for a tab opened straight into tmux: starts control mode and returns
    /// once the session's windows are loaded. On failure, with
    /// <paramref name="fallbackToShell"/> the tab starts a shell instead and says why;
    /// otherwise the session is torn down and the error thrown.
    /// </summary>
    internal async Task StartTmuxControlAsync(bool fallbackToShell)
    {
        if (_tmuxControl is not { } control) return;
        try { await control.StartAsync(); }
        catch (Exception ex) when (fallbackToShell && _client.IsConnected)
        {
            LeaveTmux(control, $"tmux: couldn't open session '{control.SessionName}' ({ex.Message}). Showing a shell instead.");
        }
        catch
        {
            Kill();
            throw;
        }
    }

    /// <summary>
    /// Shows <paramref name="session"/> in this tab through control mode, on this tab's
    /// connection; the shell keeps running underneath and comes back on detach. Already
    /// in tmux, switches to that session. Returns an error message, or null on success.
    /// </summary>
    internal async Task<string?> EnterTmuxAsync(string session)
    {
        if (_connectionEnded || !_client.IsConnected) return "Session is disconnected.";
        if (_tmuxControl is { } current)
        {
            if (current.SessionName != session) current.SwitchSession(session);
            return null;
        }

        var control = CreateTmuxControl(session);
        ModeChanged?.Invoke(this, EventArgs.Empty);
        RaiseTmuxStateChanged();
        try
        {
            await control.StartAsync();
            // The windows are loaded now: the panes to split and the terminal to focus exist.
            if (ReferenceEquals(_tmuxControl, control)) ModeChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_tmuxControl, control))
            {
                DropTmuxControl(control);
                ModeChanged?.Invoke(this, EventArgs.Empty);
                RaiseTmuxStateChanged();
            }
            return ex.Message;
        }
    }

    /// <summary>Leaves tmux mode: this client detaches, and the tab shows its shell again.</summary>
    internal void DetachTmux()
    {
        if (_tmuxControl is { } control) LeaveTmux(control, null);
    }

    /// <summary>Shows another tmux session in this tab (tmux mode).</summary>
    internal void SwitchTmuxSession(string name) => _tmuxControl?.SwitchSession(name);

    /// <summary>Renames the tmux session shown (tmux mode).</summary>
    internal void RenameTmuxSession(string name) => _tmuxControl?.RenameSession(name);

    /// <summary>Opens a new window in the tmux session shown (tmux mode).</summary>
    internal void NewTmuxWindow() => _tmuxControl?.NewWindow();

    /// <summary>Kills the tmux session shown; the tab goes back to its shell without a notice.</summary>
    internal void KillTmuxSession()
    {
        if (_tmuxControl is not { } control) return;
        _tmuxExitExpected = true;
        control.KillSession();
    }

    /// <summary>Builds the control-mode view for <paramref name="session"/> and lays it over the shell.</summary>
    private TmuxControlSession CreateTmuxControl(string session)
    {
        var definition = _definition ?? throw new InvalidOperationException("tmux mode needs the session definition.");
        var control = new TmuxControlSession(_client, session, () => TerminalFactory.Create(definition, _saveConfig));
        control.TerminalAdded += _titles.Watch;
        control.TerminalRemoved += _titles.Forget;
        control.ActivePaneChanged += (_, _) =>
        {
            if (ReferenceEquals(_tmuxControl, control)) _titles.Refresh();
        };
        control.Ended += reason => OnTmuxControlEnded(control, reason);
        control.Message += message =>
        {
            if (ReferenceEquals(_tmuxControl, control)) RaiseNotice(message);
        };
        control.SessionNameChanged += (_, _) =>
        {
            if (ReferenceEquals(_tmuxControl, control)) RaiseTmuxStateChanged();
        };
        // SFTP follows the active pane's folder, which tmux reports itself (no OSC 7 needed).
        control.ActiveDirectoryChanged += dir =>
        {
            if (ReferenceEquals(_tmuxControl, control)) _sftpView?.OnTerminalDirectoryChanged(dir);
        };
        _tmuxControl = control;
        _contentHost.Children.Add(control.View);
        SetShellShown(false);
        _titles.Refresh();
        return control;
    }

    /// <summary>
    /// The control client ended. A failed channel is a dropped connection like any other
    /// (the overlay, and Reconnect brings tmux back). When tmux ended it (detached from
    /// elsewhere, session killed, server gone), the tab goes back to its shell and says why.
    /// </summary>
    private void OnTmuxControlEnded(TmuxControlSession control, string? reason)
    {
        if (!ReferenceEquals(_tmuxControl, control)) return;
        if (!control.EndedByTmux || !_client.IsConnected)
        {
            if (_client.IsConnected && !string.IsNullOrEmpty(reason)) EndMessage = "tmux: " + reason;
            FireSessionEnded();
            return;
        }
        bool expected = _tmuxExitExpected;
        _tmuxExitExpected = false;
        LeaveTmux(control, expected ? null : $"tmux: {reason ?? "the control client ended"}. Back to the shell.");
    }

    /// <summary>Back from tmux mode to the shell, starting one if the tab never had it.</summary>
    private void LeaveTmux(TmuxControlSession control, string? notice)
    {
        if (!ReferenceEquals(_tmuxControl, control)) return;
        DropTmuxControl(control);
        if (notice is not null) RaiseNotice(notice);
        if (_panes is null) _ = StartShellAsync();
        ModeChanged?.Invoke(this, EventArgs.Empty);
        RaiseTmuxStateChanged();
    }

    /// <summary>Ends the control client (tmux detaches it) and takes its view out of the tab.</summary>
    private void DropTmuxControl(TmuxControlSession control)
    {
        if (!ReferenceEquals(_tmuxControl, control)) return;
        _tmuxControl = null;
        foreach (var tc in control.Terminals) _titles.Forget(tc);
        control.Dispose();
        _contentHost.Children.Remove(control.View);
        SetShellShown(true);
        _titles.Refresh();
        if (Terminal?.CurrentDirectory is { Length: > 0 } dir) _sftpView?.OnTerminalDirectoryChanged(dir);
    }

    /// <summary>
    /// Shows or hides the shell's panes. Hidden with opacity, not IsVisible: they stay in
    /// the tree and laid out, so the shells keep being read. Disabled while hidden, so they
    /// lose keyboard focus and no key reaches a shell nobody can see.
    /// </summary>
    private void SetShellShown(bool shown)
    {
        if (_panes is null) return;
        _panes.Opacity = shown ? 1 : 0;
        _panes.IsHitTestVisible = shown;
        _panes.IsEnabled = shown;
    }

    /// <summary>Gives the tab its shell (first pane) on this connection: the panes, then the channel.</summary>
    private void InitShell(TerminalControl tc)
    {
        _tc = tc;
        _panes = new PaneLayoutView(tc)
        {
            CellSizeProvider = () => new Avalonia.Size(Terminal?.CharWidth ?? 0, Terminal?.CharHeight ?? 0),
            SplitRequested   = SplitPane,
            CloseRequested   = pane => ClosePane((TerminalControl)pane),
        };
        // SFTP follows the active pane: on a switch, show the folder that pane is in.
        _panes.ActivePaneChanged += (_, _) =>
        {
            _titles.Refresh();
            if (Terminal?.CurrentDirectory is { Length: > 0 } dir)
                _sftpView?.OnTerminalDirectoryChanged(dir);
        };
        _paneShells[tc] = new PaneShell();
        _titles.Watch(tc);
        WatchDirectory(tc);
        _contentHost.Children.Insert(0, _panes);   // under the tmux view, if any
        SetShellShown(_tmuxControl is null);
    }

    /// <summary>A tab that opened straight into tmux has left it: start its shell now.</summary>
    private async Task StartShellAsync()
    {
        if (_connectionEnded || _definition?.Settings is not SshSettings settings) return;
        var tc = TerminalFactory.Create(_definition, _saveConfig);
        tc.Process = string.Empty;
        InitShell(tc);

        var loaded = new TaskCompletionSource();
        if (tc.IsLoaded) loaded.TrySetResult();
        else tc.Loaded += (_, _) => loaded.TrySetResult();
        try
        {
            await SshSessionLauncher.CompleteConnectionAsync(tc, _client, settings, loaded.Task, this);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SSH] shell after tmux failed: {ex.Message}");
            FireSessionEnded();
        }
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
        if (_tc is not { } tc) return;
        _connection = connection;
        _paneShells[tc].Connection = connection;

        // Both ConnectionClosed (SSH.NET detected the drop) and ProcessExited
        // (TerminalView EOF fallback) route here so the overlay always appears.
        connection.ConnectionClosed += (_, _) => OnPaneEnded(tc, connection);

        // Fallback: TerminalView raises ProcessExited when ReadAsync returns 0
        // (EOF on the shell stream). This fires even if ConnectionClosed was missed,
        // e.g. if the shell closed cleanly but ErrorOccurred was never raised.
        tc.ProcessExited += (_, _) => OnPaneEnded(tc, connection);
    }

    /// <summary>
    /// A pane's shell ended. If the connection itself went down, or it was the last
    /// pane, the session has ended; otherwise just that pane closes.
    /// </summary>
    private void OnPaneEnded(TerminalControl tc, SshPtyConnection connection)
    {
        if (connection.ClosedByError || !_client.IsConnected)
        {
            FireSessionEnded();
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (!_paneShells.ContainsKey(tc)) return;   // already closed from the UI
            if (_panes is not { PaneCount: > 1 }) FireSessionEnded();
            else ClosePane(tc);
        });
    }

    private void FireSessionEnded()
    {
        if (Interlocked.CompareExchange(ref _sessionEndedFired, 1, 0) != 0) return;
        _connectionEnded = true;
        _tmuxTimer?.Dispose();
        _sysmonPoller?.Dispose();
        _dockerMonPoller?.Dispose();
        _ = DisposeSftpConnectionAsync(_sftpConnection);
        // Close any open tail windows — their underlying exec channels are dead.
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var w in _tailWindows.Values.ToArray())
            {
                try { w.Close(); } catch { }
            }
            _tailWindows.Clear();
            SessionEnded?.Invoke(this, EventArgs.Empty);
        });
    }

    // -----------------------------------------------------------------------
    // Split panes
    // -----------------------------------------------------------------------

    /// <summary>Types a line into the active pane's shell (after Ctrl+U). Null until that shell is up.</summary>
    private Action<string>? ShellCommand =>
        _tmuxControl is { } tmux ? line => tmux.SendToActivePane("\x15" + line + "\r")
        : Terminal is { } t && _paneShells.TryGetValue(t, out var pane) ? pane.Send : null;

    private void SendToActiveShell(string line) => ShellCommand?.Invoke(line);

    /// <summary>Forwards the pane's working directory (OSC 7) to the SFTP view while it is the active pane.</summary>
    private void WatchDirectory(TerminalControl tc)
    {
        tc.PropertyChanged += (_, args) =>
        {
            if (args.Property == TerminalControl.CurrentDirectoryProperty && args.NewValue is string path)
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(tc, Terminal)) _sftpView?.OnTerminalDirectoryChanged(path);
                });
        };
    }

    /// <summary>
    /// Splits the active pane with a new shell on the same SSH connection — no new
    /// login. The new shell starts in the active pane's folder when that is known
    /// (OSC 7); the <c>cd</c> rides on the hidden shell-integration command so it
    /// doesn't show up in the terminal.
    /// </summary>
    private async void SplitPane(SplitAxis axis)
    {
        if (_connectionEnded || !_client.IsConnected || _definition is null || _panes is null) return;
        var startIn = Terminal?.CurrentDirectory;

        var tc = TerminalFactory.Create(_definition, _saveConfig);
        tc.Process = string.Empty;
        if (!_panes.AddPane(tc, axis)) return;
        _paneShells[tc] = new PaneShell();
        _titles.Watch(tc);
        WatchDirectory(tc);

        try
        {
            if (!tc.IsLoaded)
            {
                var loaded = new TaskCompletionSource();
                tc.Loaded += (_, _) => loaded.TrySetResult();
                await loaded.Task;
            }
            var term = (_definition.Settings as SshSettings)?.Term ?? "xterm-256color";
            var cols = (uint)Math.Max(20, tc.Terminal.Cols);
            var rows = (uint)Math.Max(5, tc.Terminal.Rows);
            var shell = await Task.Run(() => _client.CreateShellStream(term, cols, rows, 0, 0, 0x10000));
            var connection = new SshPtyConnection(_client, shell) { OwnsClient = false };

            if (!_paneShells.TryGetValue(tc, out var pane) || _connectionEnded)
            {
                connection.Dispose();   // pane closed (or session ended) while the channel opened
                return;
            }
            pane.Connection = connection;
            pane.Send = line => { shell.Write("\x15"); shell.WriteLine(line); };
            connection.ConnectionClosed += (_, _) => OnPaneEnded(tc, connection);
            tc.ProcessExited += (_, _) => OnPaneEnded(tc, connection);

            if (_shellIntegrationInjected)
            {
                var cd = string.IsNullOrEmpty(startIn) ? "" : $"cd {TmuxClient.Quote(startIn)} 2>/dev/null; ";
                connection.ArmShellIntegration(cd + Osc7ShellIntegrationCommand);
            }
            tc.AttachConnection(connection);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SSH] split pane failed: {ex.Message}");
            try { tc.Terminal?.Write($"\r\n\u001b[1;31mCould not open a shell: {ex.Message}\u001b[0m\r\n"); } catch { }
        }
    }

    /// <summary>Closes one pane: removes it from the layout, ends its shell and channel.</summary>
    private void ClosePane(TerminalControl tc)
    {
        if (!_paneShells.Remove(tc, out var pane)) return;
        _panes?.RemovePane(tc);
        _titles.Forget(tc);
        try { tc.Kill(); } catch { }
        try { pane.Connection?.Dispose(); } catch { }
    }

    /// <summary>
    /// Called from <see cref="SshSessionLauncher.CompleteConnectionAsync"/> once the
    /// shell stream is created. Enables bookmark activation to inject <c>cd</c> commands.
    /// </summary>
    internal void SetShellCommand(Action<string> sendCommand)
    {
        if (_tc is not null) _paneShells[_tc].Send = sendCommand;
    }

    public Control TabContent => _hostPanel;
    public TerminalControl? Terminal =>
        _tmuxControl is { } tmux ? tmux.ActiveTerminal : _panes?.ActivePane as TerminalControl;

    /// <summary>The shell's panes and, in tmux mode, tmux's: all of them move when the tab does.</summary>
    public IReadOnlyList<TerminalControl> Terminals =>
        [.. _panes?.Panes.OfType<TerminalControl>() ?? [], .. _tmuxControl?.Terminals ?? []];

    public PaneLayoutView? Panes => _tmuxControl is { } tmux ? tmux.ActivePanes : _panes;
    public Control? SftpPanel => _sftpView;
    public string Title => _titles.Current;
    public event EventHandler? TitleChanged;
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
        ShowTailWindow("file:" + remotePath,
            () => new LogTailWindow(new SshTailLogSource(_client, remotePath), remotePath));
    }

    private void OpenDockerLogsWindow(string containerId, string name)
    {
        if (_connectionEnded || !_client.IsConnected) return;
        var shortId = containerId.Length > 12 ? containerId[..12] : containerId;
        var label = $"docker:{name} ({shortId})";
        ShowTailWindow("docker:" + containerId,
            () => new LogTailWindow(new DockerLogsSource(_client, containerId, label)));
    }

    /// <summary>
    /// One window per source: brings an existing window for <paramref name="key"/>
    /// to the front, otherwise creates and shows a new one.
    /// </summary>
    private void ShowTailWindow(string key, Func<LogTailWindow> create)
    {
        if (_tailWindows.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = create();
        _tailWindows[key] = window;
        window.Closed += (_, _) => _tailWindows.Remove(key);
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
        try { _tmuxTimer?.Dispose();       } catch { }
        try { _tmuxControl?.Dispose();     } catch { }
        try { _sysmonPoller?.Dispose();    } catch { }
        try { _dockerMonPoller?.Dispose(); } catch { }
        foreach (var w in _tailWindows.Values.ToArray())
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

        // 4. Kill the pane terminals last.
        foreach (var tc in Terminals)
            try { tc.Kill(); } catch { }

        // 5. The terminal only detaches an attached connection, so release them ourselves.
        foreach (var pane in _paneShells.Values)
            try { pane.Connection?.Dispose(); } catch { }
        _paneShells.Clear();
    }

    public void Dispose() => Kill();
}
