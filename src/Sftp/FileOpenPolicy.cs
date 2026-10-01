using TermThing.Editor;

namespace TermThing.Sftp;

/// <summary>What double-clicking a remote file does.</summary>
public enum DoubleClickAction
{
    /// <summary>Open in the built-in text editor (or a catch-all default application).</summary>
    Editor,
    /// <summary>Download to temp and hand to the OS default application for its type.</summary>
    Native,
    /// <summary>Do nothing — the user can still choose Open With explicitly.</summary>
    None,
}

/// <summary>
/// Decides what a double-click on a remote file should do, from its extension and size.
/// Explicit per-extension default applications are applied before this is consulted.
/// </summary>
public static class FileOpenPolicy
{
    /// <summary>Larger text files are not loaded into the built-in editor on double-click.</summary>
    public const long EditorMaxBytes = 10L * 1024 * 1024;

    /// <summary>Larger files are not downloaded for the native opener on double-click.</summary>
    public const long NativeMaxBytes = 512L * 1024 * 1024;

    // Formats with an obvious viewer on every desktop. This is a whitelist on purpose:
    // the OS default handler for a type is "run it" for executables and scripts
    // (.exe, .bat, .vbs, .lnk, .hta, …), so nothing of that kind may ever appear here.
    private static readonly HashSet<string> Native = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif",
        ".ico", ".psd", ".xcf", ".raw", ".heic", ".heif", ".avif",
        // Video
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v",
        ".mpeg", ".mpg", ".3gp",
        // Audio
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma", ".aiff",
        // Documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".odt", ".ods", ".odp",
        // Fonts
        ".ttf", ".otf", ".woff", ".woff2",
        // Archives
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".7z", ".rar", ".zst",
    };

    // Never acted on by a double-click: running, mounting or installing a file is not
    // something a stray double-click should do, and none of these are readable as text.
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        // Executables / native binaries
        ".exe", ".dll", ".sys", ".com", ".scr", ".so", ".dylib", ".bin", ".o", ".a", ".lib",
        ".class", ".jar", ".wasm", ".pyc", ".pyd", ".elf",
        // Installers / packages
        ".msi", ".msix", ".appx", ".deb", ".rpm", ".apk", ".appimage", ".pkg",
        // Disk images
        ".iso", ".dmg", ".img", ".vhd", ".vhdx", ".vmdk", ".qcow2",
        // Databases
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb",
    };

    public static DoubleClickAction ForDoubleClick(string extension, long size)
    {
        if (Blocked.Contains(extension))
            return DoubleClickAction.None;

        if (Native.Contains(extension))
            return size <= NativeMaxBytes ? DoubleClickAction.Native : DoubleClickAction.None;

        // Any other format already known to be binary has no safe default either way.
        if (BinaryExtensions.IsLikelyBinary(extension))
            return DoubleClickAction.None;

        return size <= EditorMaxBytes ? DoubleClickAction.Editor : DoubleClickAction.None;
    }
}
