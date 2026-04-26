using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Reactive;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;
using System.IO;
using TermThing.Configuration;
using TermThing.Ssh;
using TermThing.Views;

namespace TermThing.Sessions.Launchers;

public sealed class SshSessionLauncher : ISessionLauncher
{
    private readonly IKnownHostsService _knownHosts;

    public SshSessionLauncher(IKnownHostsService knownHosts)
        => _knownHosts = knownHosts;

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
                var passphrase = await promptHost.PromptForPassphraseAsync(settings.KeyFilePath);
                if (passphrase is null)
                    throw new OperationCanceledException("User cancelled passphrase entry.");
                settings.TransientKeyPassphrase = passphrase;
            }
        }

        // Build ConnectionInfo
        ConnectionInfo connectionInfo = BuildConnectionInfo(settings);

        // Set up host-key verification before connecting
        HostKeyEventArgs? pendingKeyArgs = null;
        KnownHostStatus? pendingStatus = null;
        var hostKeyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        SshClient client = new(connectionInfo);
        client.HostKeyReceived += async (_, e) =>
        {
            var status = _knownHosts.Check(settings.Host, settings.Port, e);
            if (status == KnownHostStatus.Trusted)
            {
                e.CanTrust = true;
                hostKeyTcs.TrySetResult(true);
                return;
            }

            // Not trusted — abort the sync callback, then prompt the user asynchronously
            e.CanTrust = false;
            pendingKeyArgs = e;
            pendingStatus = status;
            hostKeyTcs.TrySetResult(false);
        };

        // First connection attempt
        await Task.Run(() => { try { client.Connect(); } catch { } }, cancellationToken);

        // Handle unknown / mismatched key
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

            // Reconnect trusting the key this time
            client.Dispose();
            client = new SshClient(connectionInfo);
            client.HostKeyReceived += (_, e) => e.CanTrust = true;
            await Task.Run(() => client.Connect(), cancellationToken);
        }

        SftpClient? sftpClient = null;
        if (settings.EnableSftp)
        {
            sftpClient = new SftpClient(connectionInfo);
            await Task.Run(() => sftpClient.Connect(), cancellationToken);
        }

        // Use the persisted font size if one has been set via Ctrl+Wheel
        var fontSize = SettingsService.Temp.TerminalFontSize > 0
            ? SettingsService.Temp.TerminalFontSize
            : 14;

        var tc = new TerminalControl
        {
            Process = string.Empty,
            Background = Brushes.Black,
            Foreground = Brushes.LightGray,
            FontFamily = FontFamily.Parse("fonts:CascadiaCode#Cascadia Code"),
            FontSize = fontSize,
        };

        // Attach terminal mouse enhancements (Ctrl+RightClick menu, Ctrl+Wheel font size)
        TerminalContextMenuBehavior.Attach(tc);

        // Wait for TerminalControl to be loaded so we can read Cols/Rows
        if (!tc.IsLoaded)
        {
            var tcs = new TaskCompletionSource<bool>();
            tc.Loaded += (_, _) => tcs.TrySetResult(true);
            // Return the instance now; the caller will add it to the visual tree
            // and LaunchConnectionAsync will complete after layout
            var instance = new SshSessionInstance(tc, definition.Name, client, sftpClient);
            _ = CompleteConnectionAsync(tc, client, sftpClient, settings, tcs.Task);
            return instance;
        }

        await CompleteConnectionAsync(tc, client, sftpClient, settings, Task.CompletedTask);
        return new SshSessionInstance(tc, definition.Name, client, sftpClient);
    }

    private static async Task CompleteConnectionAsync(
        TerminalControl tc,
        SshClient client,
        SftpClient? sftpClient,
        SshSettings settings,
        Task loadedTask)
    {
        await loadedTask;
        var cols = (uint)Math.Max(80, tc.Terminal.Cols);
        var rows = (uint)Math.Max(24, tc.Terminal.Rows);
        var shell = client.CreateShellStream(settings.Term, cols, rows, 0, 0, 0x10000);
        var connection = new SshPtyConnection(client, shell);
        await tc.AttachConnection(connection);

        if (sftpClient != null && settings.ShellIntegrationOsc7)
        {
            await Task.Run(() => shell.WriteLine(
                "PROMPT_COMMAND='printf \"\\033]7;file://${HOSTNAME}${PWD}\\007\"'"));
        }
    }

    private static ConnectionInfo BuildConnectionInfo(SshSettings s)
    {
        var authMethods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(s.KeyFilePath))
        {
            if (!File.Exists(s.KeyFilePath))
                throw new FileNotFoundException("Private key file not found.", s.KeyFilePath);

            PrivateKeyFile keyFile = string.IsNullOrEmpty(s.TransientKeyPassphrase)
                ? new PrivateKeyFile(s.KeyFilePath)
                : new PrivateKeyFile(s.KeyFilePath, s.TransientKeyPassphrase);
            authMethods.Add(new PrivateKeyAuthenticationMethod(s.Username, keyFile));
        }

        if (!string.IsNullOrEmpty(s.TransientPassword))
            authMethods.Add(new PasswordAuthenticationMethod(s.Username, s.TransientPassword));

        if (authMethods.Count == 0)
            authMethods.Add(new PasswordAuthenticationMethod(s.Username, string.Empty));

        return new ConnectionInfo(s.Host, s.Port, s.Username, [.. authMethods]);
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

    public SshSessionInstance(TerminalControl tc, string title, SshClient client, SftpClient? sftpClient)
    {
        _tc = tc;
        _client = client;
        _sftpClient = sftpClient;
        Title = title;

        if (sftpClient != null)
        {
            _sftpView = new SftpFileBrowserView(sftpClient);

            // Navigate to home directory immediately so the panel has content on first open.
            // WorkingDirectory is the SFTP session's initial directory (usually ~).
            var homeDir = sftpClient.WorkingDirectory;
            Dispatcher.UIThread.Post(() => _sftpView.NavigateTo(homeDir));

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

        tc.ProcessExited += (_, _) =>
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
        // Disconnect the SSH client first — this tears down the shell stream,
        // which signals the TerminalView reader loop to exit cleanly.
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        try { _client.Dispose(); } catch { }
        try { _sftpClient?.Dispose(); } catch { }
        // Kill the TerminalControl last; _ptyConnection may be null if AttachConnection
        // hadn't completed yet, so swallow any NullReferenceException.
        try { _tc.Kill(); } catch { }
    }

    public void Dispose() => Kill();
}
