using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TermThing.Updater;

/// <summary>
/// Downloads, verifies, and applies an update produced by the GitHub Actions release pipeline.
///
/// <para><b>Coupling note:</b> this installer assumes the release assets are single-file
/// self-contained executables packed into a <c>.zip</c> archive. If <c>PublishSingleFile</c>
/// is ever disabled in the workflow you must rewrite this class to perform a directory-level
/// swap instead of swapping a single file.</para>
/// </summary>
public static class UpdateInstaller
{
    // --------------------------------------------------------------------------
    // Public API
    // --------------------------------------------------------------------------

    /// <summary>
    /// Downloads and verifies the update package for the current platform, then
    /// launches a detached swap script and requests application shutdown via
    /// <paramref name="shutdownCallback"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No asset matching the current RID was found, or SHA256 verification failed.
    /// </exception>
    public static async Task PrepareAndApplyAsync(
        UpdateInfo info,
        IProgress<double>? progress = null,
        Action? shutdownCallback = null,
        CancellationToken ct = default)
    {
        // Locate matching zip asset
        var rid = RuntimeInformation.RuntimeIdentifier;
        var zipAsset = info.Assets.FirstOrDefault(
            a => a.Name.Contains(rid, StringComparison.OrdinalIgnoreCase)
              && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

        if (zipAsset is null)
            throw new InvalidOperationException(
                $"No release asset found for the current platform ({rid}). " +
                $"Visit {info.HtmlUrl} to download manually.");

        // Locate the SHA256SUMS.txt asset
        var sumsAsset = info.Assets.FirstOrDefault(
            a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));

        // Create a temporary working directory
        var workDir = Path.Combine(Path.GetTempPath(), $"termthing-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            using var http = new System.Net.Http.HttpClient();
            var current = VersionHelper.CurrentVersion()?.ToString() ?? "0.0.0-dev";
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"TermThing/{current}");

            // Download zip
            var zipPath = Path.Combine(workDir, zipAsset.Name);
            progress?.Report(0.05);
            await DownloadFileAsync(http, zipAsset.Url, zipPath, progress, 0.05, 0.75, ct);

            // Download and verify SHA256
            progress?.Report(0.80);
            if (sumsAsset is not null)
            {
                var sumsPath = Path.Combine(workDir, "SHA256SUMS.txt");
                await DownloadFileAsync(http, sumsAsset.Url, sumsPath, null, 0, 0, ct);
                VerifySha256(zipPath, zipAsset.Name, sumsPath);
            }

            // Extract the zip
            progress?.Report(0.85);
            var extractDir = Path.Combine(workDir, "extracted");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

            // Find the new executable inside the extracted directory
            var newExePath = FindExecutable(extractDir, rid);
            if (newExePath is null)
                throw new InvalidOperationException(
                    "Could not locate the application executable inside the downloaded archive.");

            // Current exe path
            var currentExePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot determine the path of the running executable.");

            progress?.Report(0.95);

            // Write and launch the swap script
            LaunchSwapScript(currentExePath, newExePath, Process.GetCurrentProcess().Id, workDir);

            progress?.Report(1.0);

            // Trigger app shutdown so the swap script can overwrite the locked file
            shutdownCallback?.Invoke();
        }
        catch
        {
            // Clean up work dir on failure so temp doesn't fill up with partial downloads
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort */ }
            throw;
        }
        // Note: on success we intentionally do NOT delete workDir yet — the swap script
        // still needs the new exe inside it.  The OS will reclaim it on next reboot or
        // the script deletes itself after applying the update.
    }

    // --------------------------------------------------------------------------
    // Private helpers
    // --------------------------------------------------------------------------

    private static async Task DownloadFileAsync(
        System.Net.Http.HttpClient http,
        Uri url,
        string destPath,
        IProgress<double>? progress,
        double progressStart,
        double progressEnd,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? 0L;
        using var src  = await response.Content.ReadAsStreamAsync(ct);
        using var dest = File.Create(destPath);

        var buffer    = new byte[81920];
        long written  = 0;
        int  read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            if (progress != null && totalBytes > 0)
            {
                var fraction = (double)written / totalBytes;
                progress.Report(progressStart + fraction * (progressEnd - progressStart));
            }
        }
    }

    private static void VerifySha256(string filePath, string assetName, string sumsFilePath)
    {
        // SHA256SUMS.txt format: "<hex>  <filename>" one line per file
        string? expected = null;
        foreach (var line in File.ReadLines(sumsFilePath))
        {
            var parts = line.Trim().Split("  ", 2, StringSplitOptions.None);
            if (parts.Length == 2 &&
                parts[1].Equals(assetName, StringComparison.OrdinalIgnoreCase))
            {
                expected = parts[0].Trim().ToLowerInvariant();
                break;
            }
        }

        if (expected is null)
            throw new InvalidOperationException(
                $"SHA256SUMS.txt does not contain an entry for '{assetName}'. " +
                "The release may be malformed.");

        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(filePath);
        var hash = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();

        if (hash != expected)
            throw new InvalidOperationException(
                $"SHA256 mismatch for '{assetName}'.\n" +
                $"  Expected: {expected}\n" +
                $"  Actual:   {hash}\n" +
                "The download may be corrupt or tampered with. Update aborted.");
    }

    private static string? FindExecutable(string extractDir, string rid)
    {
        // The single-file publish produces exactly one file that is the exe.
        // On Windows it ends in .exe; on POSIX the extension is absent.
        var files = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return files.FirstOrDefault(f =>
                f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));

        // On POSIX prefer an exact match by RID, then fall back to the sole non-pdb file
        var byRid = files.FirstOrDefault(f =>
            Path.GetFileName(f).Contains(rid, StringComparison.OrdinalIgnoreCase));
        if (byRid is not null) return byRid;

        var candidates = files.Where(f =>
            !f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    // --------------------------------------------------------------------------
    // OS-specific swap scripts
    // --------------------------------------------------------------------------

    private static void LaunchSwapScript(
        string oldExe, string newExe, int parentPid, string workDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            LaunchWindowsSwap(oldExe, newExe, workDir);
        else
            LaunchPosixSwap(oldExe, newExe, parentPid, workDir);
    }

    private static void LaunchWindowsSwap(string oldExe, string newExe, string workDir)
    {
        var scriptPath = Path.Combine(workDir, "updater.cmd");

        // The script:
        //   1. Waits until the old exe can be deleted (process has exited).
        //   2. Moves the new exe into place.
        //   3. Relaunches.
        //   4. Deletes itself.
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine(":wait");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine($"del /Q \"{oldExe}\" 2>nul");
        sb.AppendLine($"if exist \"{oldExe}\" goto wait");
        sb.AppendLine($"move /Y \"{newExe}\" \"{oldExe}\"");
        sb.AppendLine($"start \"\" \"{oldExe}\"");
        sb.AppendLine("del \"%~f0\"");

        File.WriteAllText(scriptPath, sb.ToString(), Encoding.ASCII);

        Process.Start(new ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void LaunchPosixSwap(string oldExe, string newExe, int parentPid, string workDir)
    {
        var scriptPath = Path.Combine(workDir, "updater.sh");

        // The script:
        //   1. Waits for the parent process (TermThing) to exit.
        //   2. Replaces the old exe with the new one.
        //   3. Relaunches.
        //   4. Deletes itself.
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env sh");
        sb.AppendLine("set -e");
        sb.AppendLine($"OLD=\"{oldExe}\"");
        sb.AppendLine($"NEW=\"{newExe}\"");
        sb.AppendLine($"PID={parentPid}");
        sb.AppendLine("while kill -0 \"$PID\" 2>/dev/null; do sleep 0.2; done");
        sb.AppendLine("mv -f \"$NEW\" \"$OLD\"");
        sb.AppendLine("chmod +x \"$OLD\"");
        sb.AppendLine("\"$OLD\" &");
        sb.AppendLine("rm -- \"$0\"");

        File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // Make the script executable (Unix file mode 0755)
        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Process.Start(new ProcessStartInfo
        {
            FileName        = "/bin/sh",
            Arguments       = $"\"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }
}
