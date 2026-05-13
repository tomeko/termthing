using Renci.SshNet;
using Renci.SshNet.Common;
using System.Diagnostics;
using System.Net;
using TermThing.Sessions;
using TermThing.Views;

namespace TermThing.Ssh;

/// <summary>
/// Connects an ordered chain of SSH jump-hops using SSH.NET's
/// <see cref="ForwardedPortLocal"/> and returns a <see cref="SshChainResult"/> that
/// the caller (launcher) uses to open a shell on the final target.
/// </summary>
/// <remarks>
/// <para>
/// For a chain [J1, J2, …, Jn] → Target the algorithm is:
/// <list type="number">
///   <item>Connect <see cref="SshClient"/> directly to J1 on its real TCP address.</item>
///   <item>
///     For each subsequent node (including Target): start a
///     <see cref="ForwardedPortLocal"/> on the <em>previous</em> client whose remote
///     endpoint is the logical host/port of the next node, bind it to an OS-assigned
///     loopback port, then build the next <see cref="SshClient"/> against
///     <c>127.0.0.1:&lt;BoundPort&gt;</c>.
///   </item>
///   <item>
///     Host-key verification always uses the <strong>logical</strong> host/port so that
///     <see cref="IKnownHostsService"/> lookups remain stable across reconnects
///     (ephemeral ports change each time).
///   </item>
/// </list>
/// </para>
/// <para>
/// On any failure the connector disposes all resources that were successfully created,
/// in reverse order, before rethrowing.
/// </para>
/// </remarks>
internal static class SshChainConnector
{
    /// <summary>
    /// Builds the jump chain, connects every hop in order, then returns a
    /// <see cref="SshChainResult"/> containing the fully-connected final client plus
    /// the forwarded-port and intermediate-client stacks needed for cleanup.
    /// </summary>
    /// <param name="hops">Ordered resolved jump hops (excluding the final target).</param>
    /// <param name="targetSettings">The final destination SSH settings.</param>
    /// <param name="knownHosts">Host-key trust store.</param>
    /// <param name="promptHost">UI host for credential / host-key prompts.</param>
    /// <param name="progress">Optional callback for status messages shown to the user.</param>
    /// <param name="cancellationToken">Propagated to every async wait.</param>
    public static async Task<SshChainResult> ConnectAsync(
        IReadOnlyList<ResolvedHop> hops,
        SshSettings                targetSettings,
        IKnownHostsService         knownHosts,
        ISessionPromptHost         promptHost,
        Action<string>?            progress          = null,
        CancellationToken          cancellationToken = default)
    {
        // Stacks for cleanup on failure (reversed at the end / in catch).
        var clients  = new List<SshClient>();
        var forwards = new List<ForwardedPortLocal>();

        try
        {
            SshClient? parentClient = null;

            // ----------------------------------------------------------------
            // Connect each jump hop in order.
            // ----------------------------------------------------------------
            for (int i = 0; i < hops.Count; i++)
            {
                var hop = hops[i];
                cancellationToken.ThrowIfCancellationRequested();

                // Ensure we have credentials for this hop.
                await EnsureHopCredentialsAsync(hop, i + 1, promptHost, cancellationToken);

                progress?.Invoke($"Connecting to {hop.DisplayName}…");
                var sw = Stopwatch.StartNew();

                ConnectionInfo ci;
                if (parentClient is null)
                {
                    // First hop — connect directly.
                    ci = SshConnectionInfoFactory.Build(
                        hop.LogicalHost, hop.LogicalPort, hop.Username,
                        hop.KeyFilePath, hop.TransientKeyPassphrase, hop.TransientPassword);
                }
                else
                {
                    // Subsequent hop — tunnel through the previous client.
                    var fwd = StartForward(parentClient, hop.LogicalHost, hop.LogicalPort);
                    forwards.Add(fwd);
                    // Build ConnectionInfo targeting the loopback alias but with the
                    // logical host/port embedded so host-key events carry the right address.
                    ci = SshConnectionInfoFactory.Build(
                        IPAddress.Loopback.ToString(), (int)fwd.BoundPort, hop.Username,
                        hop.KeyFilePath, hop.TransientKeyPassphrase, hop.TransientPassword);
                }

                var hopClient = await ConnectClientAsync(
                    ci, hop.LogicalHost, hop.LogicalPort, knownHosts, promptHost,
                    cancellationToken);
                clients.Add(hopClient);
                parentClient = hopClient;

                Debug.WriteLine($"[SSH jump] Hop {i + 1}/{hops.Count} ({hop.DisplayName}): {sw.ElapsedMilliseconds} ms");
            }

            // ----------------------------------------------------------------
            // Connect the final target through the last jump (or directly).
            // ----------------------------------------------------------------
            cancellationToken.ThrowIfCancellationRequested();

            // Ensure credentials for the target are ready (the launcher already
            // prompts before calling us, but handle the edge case defensively).
            progress?.Invoke($"Connecting to {targetSettings.Host}…");

            ConnectionInfo targetCi;
            if (parentClient is null)
            {
                // No hops — caller should not have invoked us, but handle gracefully.
                targetCi = SshConnectionInfoFactory.Build(
                    targetSettings.Host, targetSettings.Port, targetSettings.Username,
                    targetSettings.KeyFilePath, targetSettings.TransientKeyPassphrase,
                    targetSettings.TransientPassword);
            }
            else
            {
                var finalFwd = StartForward(parentClient, targetSettings.Host, targetSettings.Port);
                forwards.Add(finalFwd);
                targetCi = SshConnectionInfoFactory.Build(
                    IPAddress.Loopback.ToString(), (int)finalFwd.BoundPort, targetSettings.Username,
                    targetSettings.KeyFilePath, targetSettings.TransientKeyPassphrase,
                    targetSettings.TransientPassword);
            }

            var finalClient = await ConnectClientAsync(
                targetCi, targetSettings.Host, targetSettings.Port, knownHosts, promptHost,
                cancellationToken);

            progress?.Invoke($"Connected.");
            return new SshChainResult(finalClient, targetCi, [.. clients], [.. forwards]);
        }
        catch
        {
            // Dispose in reverse order: forwards first, then clients.
            for (int i = forwards.Count - 1; i >= 0; i--)
            {
                try { forwards[i].Stop();    } catch { }
                try { forwards[i].Dispose(); } catch { }
            }
            for (int i = clients.Count - 1; i >= 0; i--)
            {
                try { if (clients[i].IsConnected) clients[i].Disconnect(); } catch { }
                try { clients[i].Dispose(); } catch { }
            }
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ForwardedPortLocal StartForward(SshClient parent, string remoteHost, int remotePort)
    {
        var fwd = new ForwardedPortLocal(IPAddress.Loopback.ToString(), (uint)0, remoteHost, (uint)remotePort);
        parent.AddForwardedPort(fwd);
        fwd.Start();
        return fwd;
    }

    private static async Task<SshClient> ConnectClientAsync(
        ConnectionInfo     ci,
        string             logicalHost,
        int                logicalPort,
        IKnownHostsService knownHosts,
        ISessionPromptHost promptHost,
        CancellationToken  ct)
    {
        HostKeyEventArgs? pendingArgs   = null;
        KnownHostStatus?  pendingStatus = null;

        var client = new SshClient(ci);
        client.HostKeyReceived += (_, e) =>
        {
            var status = knownHosts.Check(logicalHost, logicalPort, e);
            if (status == KnownHostStatus.Trusted)
            {
                e.CanTrust = true;
                return;
            }
            e.CanTrust    = false;
            pendingArgs   = e;
            pendingStatus = status;
        };

        await Task.Run(() => { try { client.Connect(); } catch { } }, ct);

        if (pendingArgs is not null)
        {
            // Unknown or mismatched host key — prompt the user.
            var action = await promptHost.PromptHostKeyAsync(logicalHost, logicalPort,
                pendingStatus!.Value, pendingArgs);

            if (action == HostKeyAction.Cancel)
            {
                client.Dispose();
                throw new OperationCanceledException($"SSH connection to {logicalHost} aborted by user.");
            }

            if (action == HostKeyAction.TrustAndConnect)
                knownHosts.Trust(logicalHost, logicalPort, pendingArgs);

            // Reconnect now trusting the key.
            client.Dispose();
            client = new SshClient(ci);
            client.HostKeyReceived += (_, e) => e.CanTrust = true;
            await Task.Run(() => client.Connect(), ct);
        }

        if (!client.IsConnected)
        {
            client.Dispose();
            throw new Exception($"Failed to connect to {logicalHost}:{logicalPort}.");
        }

        return client;
    }

    private static async Task EnsureHopCredentialsAsync(
        ResolvedHop       hop,
        int               hopNumber,
        ISessionPromptHost promptHost,
        CancellationToken  ct)
    {
        // Step 1: if no key file and no cached password, show the full secrets dialog.
        if (string.IsNullOrWhiteSpace(hop.KeyFilePath) &&
            string.IsNullOrEmpty(hop.TransientPassword))
        {
            // Synthesise a temporary SessionDefinition so the existing
            // PromptForSshSecretsAsync dialog can be reused unchanged.
            var tempSettings = new Sessions.SshSettings
            {
                Host        = hop.LogicalHost,
                Port        = hop.LogicalPort,
                Username    = hop.Username,
                KeyFilePath = hop.KeyFilePath,
            };
            var tempDef = new Sessions.SessionDefinition
            {
                Name     = $"Jump hop {hopNumber}: {hop.DisplayName}",
                Kind     = Sessions.SessionKind.Ssh,
                Settings = tempSettings,
            };

            var confirmed = await promptHost.PromptForSshSecretsAsync(tempDef);
            if (!confirmed)
                throw new OperationCanceledException($"User cancelled credentials for jump hop {hopNumber} ({hop.DisplayName}).");

            // Copy secrets back into the hop so they survive the connection loop.
            hop.JumpHop.TransientPassword      = tempSettings.TransientPassword;
            hop.JumpHop.TransientKeyPassphrase = tempSettings.TransientKeyPassphrase;
        }

        // Step 2: if a key file is set but no passphrase yet, probe it — the key may
        // be encrypted even if we skipped Step 1 above (mirrors launcher logic).
        if (!string.IsNullOrWhiteSpace(hop.KeyFilePath) &&
            string.IsNullOrEmpty(hop.TransientKeyPassphrase))
        {
            try { _ = new Renci.SshNet.PrivateKeyFile(hop.KeyFilePath); }
            catch (Exception ex) when (
                ex is Renci.SshNet.Common.SshPassPhraseNullOrEmptyException ||
                ex.Message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("encrypted",  StringComparison.OrdinalIgnoreCase))
            {
                var pp = await promptHost.PromptForPassphraseAsync(hop.KeyFilePath, hop.DisplayName);
                if (pp is null)
                    throw new OperationCanceledException($"User cancelled passphrase for jump hop {hopNumber}.");
                hop.JumpHop.TransientKeyPassphrase = pp;
            }
        }
    }

}

// ---------------------------------------------------------------------------

/// <summary>
/// Returned by <see cref="SshChainConnector.ConnectAsync"/> when the entire chain
/// connects successfully.
/// </summary>
/// <param name="FinalClient">
/// The fully-authenticated <see cref="SshClient"/> targeting the destination host
/// (may be connected via a loopback port-forward).
/// </param>
/// <param name="FinalConnectionInfo">
/// The <see cref="ConnectionInfo"/> used to open <see cref="FinalClient"/> — retained
/// so that a second <see cref="SftpClient"/> can be constructed through its own forward.
/// </param>
/// <param name="JumpClients">Intermediate hop clients, in connection order (J1…Jn).</param>
/// <param name="Forwards">
/// All <see cref="ForwardedPortLocal"/> instances, in creation order, that must be
/// stopped before the owning client is disconnected.
/// </param>
internal sealed record SshChainResult(
    SshClient                     FinalClient,
    ConnectionInfo                FinalConnectionInfo,
    IReadOnlyList<SshClient>      JumpClients,
    IReadOnlyList<ForwardedPortLocal> Forwards);
