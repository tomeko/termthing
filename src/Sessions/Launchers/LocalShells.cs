using System.IO;
using System.Runtime.InteropServices;

namespace TermThing.Sessions.Launchers;

/// <summary>
/// Detects available local shells at application startup.
/// On Windows, <see cref="Available"/> is populated for the shell-picker dropdown.
/// On Linux/macOS, <see cref="Default"/> contains the shell to auto-launch.
/// </summary>
public static class LocalShells
{
    /// <summary>A shell that can be launched as a local terminal session.</summary>
    public record ShellOption(string Label, string Process, string[] Args);

    private static readonly List<ShellOption> _available = [];

    /// <summary>
    /// Available shells on Windows (populated by <see cref="Detect"/>).
    /// Always empty on non-Windows — use <see cref="Default"/> there.
    /// </summary>
    public static IReadOnlyList<ShellOption> Available => _available;

    /// <summary>
    /// The best shell to auto-launch on Linux/macOS (derived from <c>$SHELL</c>
    /// then platform defaults). On Windows this is set to <c>cmd.exe</c> as a fallback.
    /// </summary>
    public static ShellOption Default { get; private set; } = new("bash", "bash", []);

    /// <summary>
    /// Call once at application startup before any local session is launched.
    /// Probes the filesystem so disk I/O is done upfront, not on the hot path.
    /// </summary>
    public static void Detect()
    {
        _available.Clear();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            DetectWindows();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            DetectUnix("/bin/zsh", "/bin/bash", "/bin/sh");
        else
            DetectUnix("/bin/bash", "/bin/sh");
    }

    // -----------------------------------------------------------------------
    // Platform detection
    // -----------------------------------------------------------------------

    private static void DetectWindows()
    {
        _available.Add(new ShellOption("Command Prompt", "cmd.exe", []));

        var ps = FindPowerShell();
        var psLabel = ps.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase)
            ? "PowerShell"
            : "Windows PowerShell";
        _available.Add(new ShellOption(psLabel, ps, []));

        var gitBash = FindGitBash();
        if (gitBash is not null)
            _available.Add(new ShellOption("Git Bash", gitBash, ["--login", "-i"]));

        Default = _available[0];
    }

    /// <summary>
    /// For Linux and macOS: try <c>$SHELL</c> first, then the supplied fallback
    /// paths in order, and set <see cref="Default"/> to the first one found.
    /// </summary>
    private static void DetectUnix(params string[] fallbackPaths)
    {
        var envShell = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrEmpty(envShell) && File.Exists(envShell))
        {
            Default = new ShellOption(Path.GetFileName(envShell), envShell, []);
            return;
        }

        foreach (var path in fallbackPaths)
        {
            if (File.Exists(path))
            {
                Default = new ShellOption(Path.GetFileName(path), path, []);
                return;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Windows helpers
    // -----------------------------------------------------------------------

    private static string FindPowerShell()
    {
        // Prefer PowerShell 7+ (pwsh.exe) over legacy Windows PowerShell 5.x.
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(pf, "PowerShell", "7", "pwsh.exe"),
            Path.Combine(pf, "PowerShell", "7-preview", "pwsh.exe"),
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        return "powershell.exe"; // always available on Windows 10+
    }

    private static string? FindGitBash()
    {
        var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new[]
        {
            Path.Combine(pf,   "Git", "bin", "bash.exe"),
            Path.Combine(pf86, "Git", "bin", "bash.exe"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
