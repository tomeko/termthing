namespace TermThing.Editor;

/// <summary>
/// Classifies file extensions as "likely binary" so the built-in text editor
/// knows when to fall back to the OS open-with dialog instead of trying to
/// display unreadable bytes.
/// </summary>
internal static class BinaryExtensions
{
    private static readonly HashSet<string> _binary = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif",
        ".ico", ".psd", ".xcf", ".raw", ".heic", ".heif", ".avif",
        // Video
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".m4v",
        ".mpeg", ".mpg", ".3gp", ".ts",
        // Audio
        ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus", ".wma", ".aiff",
        // Archives
        ".zip", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".7z", ".rar", ".zst",
        // Executables / native binaries
        ".exe", ".dll", ".so", ".dylib", ".bin", ".o", ".a", ".lib",
        ".class", ".jar", ".wasm", ".pyc", ".pyd", ".elf",
        // Office / documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".odt", ".ods", ".odp",
        // Disk images
        ".iso", ".dmg", ".img", ".vhd", ".vmdk",
        // Fonts
        ".ttf", ".otf", ".woff", ".woff2", ".eot",
        // Databases
        ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb",
    };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="extension"/> (e.g. ".png")
    /// is a known binary/non-text format that should not be opened in the built-in
    /// text editor.
    /// </summary>
    public static bool IsLikelyBinary(string extension) => _binary.Contains(extension);
}
