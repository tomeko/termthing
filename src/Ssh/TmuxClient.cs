using Renci.SshNet;

namespace TermThing.Ssh;

/// <summary>One tmux session on the remote host, from <c>tmux list-sessions</c>.</summary>
public sealed record TmuxSessionInfo(string Name, int Windows, int AttachedClients);

/// <summary>
/// The tmux client running inside this SSH connection's shell: the terminal device
/// tmux knows it by and the session it is showing.
/// </summary>
public sealed record TmuxOwnClient(string ClientTty, string SessionName);

/// <summary>
/// tmux queries and commands over SSH exec channels, side by side with the user's
/// shell (the same way the Docker and Sysmon pollers work). Everything here runs
/// <c>tmux</c> under <c>sh -c</c>, so the remote login shell (bash, zsh, fish) does
/// not matter. All methods block; call them off the UI thread.
/// </summary>
public static class TmuxClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    // Non-interactive exec channels skip the profile, so a Homebrew or /usr/local tmux
    // may not be on PATH there even though the interactive shell finds it.
    private const string PathPrefix = "PATH=\"$PATH:/usr/local/bin:/opt/homebrew/bin\"; ";

    /// <summary>Returns the <c>tmux -V</c> string, or null if tmux isn't installed.</summary>
    public static string? Probe(SshClient client)
    {
        var (exit, output) = Run(client, "tmux -V 2>/dev/null");
        output = output.Trim();
        return exit == 0 && output.StartsWith("tmux", StringComparison.Ordinal) ? output : null;
    }

    /// <summary>Lists sessions; empty when no tmux server is running.</summary>
    public static IReadOnlyList<TmuxSessionInfo> ListSessions(SshClient client)
    {
        // Name last so a name containing spaces survives the split.
        var (exit, output) = Run(client,
            "tmux list-sessions -F '#{session_windows} #{session_attached} #{session_name}' 2>/dev/null");
        if (exit != 0) return [];
        var result = new List<TmuxSessionInfo>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split(' ', 3);
            if (p.Length < 3) continue;
            result.Add(new TmuxSessionInfo(
                p[2],
                int.TryParse(p[0], out var w) ? w : 0,
                int.TryParse(p[1], out var a) ? a : 0));
        }
        return result;
    }

    /// <summary>
    /// Script that finds the tmux client started from this SSH connection's shell.
    /// <para>
    /// Every channel of one SSH connection (the interactive shell and each exec) is
    /// served by the same per-connection <c>sshd</c> process (<c>sshd-session</c> on
    /// OpenSSH 9.8+). So: walk up from this exec to its sshd, then walk up from each
    /// tmux client; a client whose nearest sshd ancestor is the same process belongs to
    /// our shell. A client under a nested <c>ssh localhost</c> stops at a different
    /// sshd and is correctly left out.
    /// </para>
    /// <para>No single quotes inside: the whole thing is passed through <c>sh -c '…'</c>.</para>
    /// </summary>
    private const string OwnClientScript =
        "anc() { p=$1; i=0; " +
        "while [ -n \"$p\" ] && [ \"$p\" -gt 1 ] && [ $i -lt 32 ]; do " +
            "case \"$(ps -o comm= -p \"$p\" 2>/dev/null)\" in sshd*|*/sshd*) echo \"$p\"; return;; esac; " +
            "p=$(ps -o ppid= -p \"$p\" 2>/dev/null | tr -d \" \"); i=$((i+1)); " +
        "done; }; " +
        "me=$(anc $$); [ -n \"$me\" ] || exit 0; " +
        "tmux list-clients -F \"#{client_pid} #{client_tty} #{session_name}\" 2>/dev/null | " +
        "while read -r pid tty name; do [ \"$(anc \"$pid\")\" = \"$me\" ] && echo \"$tty $name\"; done";

    /// <summary>
    /// The tmux client attached from this connection's shell, or null when the shell
    /// isn't inside tmux (or the process tree can't be read).
    /// </summary>
    public static TmuxOwnClient? FindOwnClient(SshClient client)
    {
        var (_, output) = Run(client, OwnClientScript);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split(' ', 2);
            if (p.Length == 2 && p[0].StartsWith('/')) return new TmuxOwnClient(p[0], p[1]);
        }
        return null;
    }

    /// <summary>Points an existing tmux client at another session (no nesting).</summary>
    public static string? SwitchClient(SshClient client, string clientTty, string session) =>
        RunChecked(client, $"tmux switch-client -c {Quote(clientTty)} -t {Quote(ExactTarget(session))}");

    /// <summary>Creates a detached session; returns its name (tmux picks one if <paramref name="name"/> is null).</summary>
    public static string? NewDetachedSession(SshClient client, string? name, out string? error)
    {
        var nameArg = string.IsNullOrEmpty(name) ? "" : $" -s {Quote(name)}";
        var (exit, output) = Run(client, $"tmux new-session -d -P -F '#{{session_name}}'{nameArg} 2>&1");
        error = exit == 0 ? null : output.Trim();
        return exit == 0 ? output.Trim() : null;
    }

    /// <summary>Detaches the given client; the shell underneath comes back.</summary>
    public static string? DetachClient(SshClient client, string clientTty) =>
        RunChecked(client, $"tmux detach-client -t {Quote(clientTty)}");

    /// <summary>
    /// Shell line that attaches the terminal it's typed into to <paramref name="session"/>,
    /// creating the session if it no longer exists (e.g. after a reboot). The leading space
    /// keeps it out of shell history where <c>ignorespace</c> is set.
    /// </summary>
    public static string AttachCommand(string session) => $" tmux new-session -A -s {Quote(session)}";

    /// <summary>Shell line that starts a new session in the terminal it's typed into.</summary>
    public static string NewSessionCommand(string? name) =>
        string.IsNullOrEmpty(name) ? " tmux new-session" : AttachCommand(name);

    // "=name" matches the session name exactly instead of as a prefix.
    private static string ExactTarget(string session) => "=" + session;

    /// <summary>POSIX single-quote escaping.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    private static string? RunChecked(SshClient client, string command)
    {
        var (exit, output) = Run(client, command + " 2>&1");
        return exit == 0 ? null : (string.IsNullOrWhiteSpace(output) ? $"tmux exited with {exit}" : output.Trim());
    }

    /// <summary>
    /// An exec-channel command running <paramref name="script"/> under <c>sh -c</c> with the
    /// extended PATH. A script that ends in a long-running program should <c>exec</c> it.
    /// </summary>
    public static SshCommand CreateCommand(SshClient client, string script) =>
        client.CreateCommand("sh -c " + Quote(PathPrefix + script));

    /// <summary>
    /// True for tmux 3.2 and later (<c>tmux 3.2</c>, <c>tmux 3.3a</c>, <c>tmux next-3.5</c>):
    /// control mode with flow control and the notifications TermThing relies on.
    /// </summary>
    public static bool SupportsControlMode(string? version)
    {
        if (version is null) return false;
        var m = System.Text.RegularExpressions.Regex.Match(version, @"(\d+)\.(\d+)");
        if (!m.Success) return version.Contains("master", StringComparison.Ordinal);
        int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
        return major > 3 || (major == 3 && minor >= 2);
    }

    private static (int Exit, string Output) Run(SshClient client, string script)
    {
        if (!client.IsConnected) return (-1, string.Empty);
        using var cmd = CreateCommand(client, script);
        cmd.CommandTimeout = Timeout;
        var output = cmd.Execute();
        return (cmd.ExitStatus ?? -1, output);
    }
}
