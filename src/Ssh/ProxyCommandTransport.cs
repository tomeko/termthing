using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TermThing.Ssh;

/// <summary>
/// OpenSSH-style <c>ProxyCommand</c> transport. Spawns a process whose stdio acts
/// as the TCP connection to the SSH server, and bridges that stdio to a loopback
/// TCP listener so SSH.NET (which only knows how to connect to a TCP endpoint)
/// can talk through it.
///
/// Flow:
///   • Token-substitute the command (<c>%h</c> host, <c>%p</c> port, <c>%r</c> user).
///   • Start the process with stdin/stdout/stderr redirected.
///   • Open a one-shot TCP listener on loopback at an OS-assigned port.
///   • On accept: spin up two pumps copying bytes both ways between the socket
///     and the process's stdin/stdout streams. Mirror stderr to debug output.
///
/// Caller is responsible for handing <see cref="LoopbackEndpoint"/> to SSH.NET's
/// <c>ConnectionInfo</c>, then awaiting <see cref="DisposeAsync"/> when done.
/// </summary>
public sealed class ProxyCommandTransport : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private int _disposed;

    public IPEndPoint LoopbackEndpoint { get; }

    /// <summary>Substituted command, kept for diagnostics.</summary>
    public string ResolvedCommand { get; }

    private ProxyCommandTransport(Process process, TcpListener listener, IPEndPoint endpoint, string resolvedCommand)
    {
        _process = process;
        _listener = listener;
        LoopbackEndpoint = endpoint;
        ResolvedCommand = resolvedCommand;
    }

    public static async Task<ProxyCommandTransport> StartAsync(
        string commandTemplate,
        string host,
        int port,
        string user,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(commandTemplate))
            throw new ArgumentException("ProxyCommand template is empty.", nameof(commandTemplate));

        var resolved = SubstituteTokens(commandTemplate, host, port, user);
        var (exe, args) = SplitCommandLine(resolved);

        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start ProxyCommand: {exe}");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ProxyCommand failed to start: {ex.Message}\n\nResolved command: {resolved}", ex);
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var transport = new ProxyCommandTransport(process, listener, endpoint, resolved);
        transport._acceptTask = transport.RunAcceptLoopAsync();

        // Mirror stderr lines (one read loop, fire-and-forget — token cancels on dispose).
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync(transport._cts.Token).ConfigureAwait(false)) is not null)
                    Debug.WriteLine($"[proxy] {line}");
            }
            catch { /* expected on dispose */ }
        }, transport._cts.Token);

        await Task.CompletedTask;
        return transport;
    }

    private async Task RunAcceptLoopAsync()
    {
        Socket? client = null;
        try
        {
            // We only ever expect one connection from SSH.NET — accept it and stop listening.
            client = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
            _listener.Stop();

            var processIn  = _process.StandardInput.BaseStream;
            var processOut = _process.StandardOutput.BaseStream;
            using var socketStream = new NetworkStream(client, ownsSocket: true);

            var socketToProcess = PumpAsync(socketStream, processIn, "sock→proc", _cts.Token);
            var processToSocket = PumpAsync(processOut, socketStream, "proc→sock", _cts.Token);

            await Task.WhenAny(socketToProcess, processToSocket).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* dispose path */ }
        catch (Exception ex) { Debug.WriteLine($"[proxy] accept loop error: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            try { client?.Dispose(); } catch { }
            try { _listener.Stop(); } catch { }
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, string label, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int n = await from.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;
                await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }
        catch (IOException) { /* socket/process closed mid-read; treat as EOF */ }
        catch (ObjectDisposedException) { /* same */ }
        catch (Exception ex) { Debug.WriteLine($"[proxy] {label} pump error: {ex.GetType().Name}: {ex.Message}"); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }

        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { }
        }

        TryKill(_process);
        try { _process.Dispose(); } catch { }
        _cts.Dispose();
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    // -----------------------------------------------------------------------
    // Token substitution + command-line splitting
    // -----------------------------------------------------------------------

    internal static string SubstituteTokens(string template, string host, int port, string user)
    {
        var sb = new StringBuilder(template.Length + 16);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '%' || i + 1 >= template.Length) { sb.Append(c); continue; }
            char next = template[i + 1];
            switch (next)
            {
                case 'h': sb.Append(host); i++; break;
                case 'p': sb.Append(port.ToString(System.Globalization.CultureInfo.InvariantCulture)); i++; break;
                case 'r': sb.Append(user); i++; break;
                case '%': sb.Append('%'); i++; break;
                default:  sb.Append(c); break; // unknown — pass through literally
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Minimal command-line splitter that respects double-quoted segments. The first
    /// (unquoted or quoted) token becomes the executable; the rest is the args string.
    /// </summary>
    internal static (string Exe, string Args) SplitCommandLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) throw new ArgumentException("ProxyCommand is empty after substitution.");

        int i = 0;
        string exe;
        if (line[0] == '"')
        {
            int end = line.IndexOf('"', 1);
            if (end < 0) throw new ArgumentException("Unterminated quote in ProxyCommand.");
            exe = line.Substring(1, end - 1);
            i = end + 1;
        }
        else
        {
            int sp = line.IndexOf(' ');
            if (sp < 0) { exe = line; i = line.Length; }
            else        { exe = line[..sp]; i = sp; }
        }

        string args = i < line.Length ? line[i..].TrimStart() : string.Empty;
        return (exe, args);
    }
}
