using System.Runtime.InteropServices;

namespace TermThing.Sftp;

/// <summary>
/// Cross-platform helper that asks the OS to open a local file using whatever
/// handler is registered for its type (the "Open With…" OS dialog or default app).
/// </summary>
public static class OsFileLauncher
{
    /// <summary>
    /// Opens <paramref name="localPath"/> using the OS default handler.
    /// On Windows uses <c>rundll32 shell32.dll,OpenAs_RunDLL</c> so the user sees
    /// the "Open With" chooser rather than silently falling back to Notepad.
    /// On macOS/Linux uses <c>open</c> / <c>xdg-open</c>.
    /// </summary>
    public static void OpenWithOsDialog(string localPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "rundll32.exe",
                Arguments       = $"shell32.dll,OpenAs_RunDLL \"{localPath}\"",
                UseShellExecute = false,
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            System.Diagnostics.Process.Start("open", $"\"{localPath}\"");
        }
        else
        {
            // Linux — xdg-open hands off to the desktop environment's default handler.
            // A native "Open With" picker is not universally available here.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "xdg-open",
                Arguments       = $"\"{localPath}\"",
                UseShellExecute = false,
            });
        }
    }

    /// <summary>
    /// Opens <paramref name="localPath"/> in the OS default application for its type,
    /// without a chooser. Only call this for types whose default handler is a viewer —
    /// for executables and scripts the default handler runs them.
    /// </summary>
    public static void OpenWithDefaultApp(string localPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = localPath,
                UseShellExecute = true,
            });
        }
        else
        {
            // open / xdg-open already use the default application.
            OpenWithOsDialog(localPath);
        }
    }

    /// <summary>
    /// Launches <paramref name="appPath"/> with the given <paramref name="localFile"/>.
    /// If <paramref name="argsTemplate"/> contains <c>{file}</c> it is replaced by the
    /// quoted local file path; otherwise the file path is appended as a lone argument.
    /// </summary>
    public static void LaunchWith(string appPath, string argsTemplate, string localFile)
    {
        string args = string.IsNullOrWhiteSpace(argsTemplate)
            ? $"\"{localFile}\""
            : argsTemplate.Replace("{file}", $"\"{localFile}\"");

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = appPath,
            Arguments       = args,
            UseShellExecute = false,
        });
    }
}
