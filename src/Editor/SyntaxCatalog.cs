using TextMateSharp.Grammars;

namespace TermThing.Editor;

/// <summary>
/// Wraps <see cref="RegistryOptions"/> to provide extension-to-grammar mapping
/// and a shared options instance (grammar loading is expensive).
/// </summary>
internal static class SyntaxCatalog
{
    private static RegistryOptions? _options;
    private static readonly object _lock = new();

    /// <summary>
    /// Returns the shared <see cref="RegistryOptions"/> instance (lazy, thread-safe).
    /// </summary>
    public static RegistryOptions GetRegistryOptions()
    {
        if (_options is null)
            lock (_lock)
                _options ??= new RegistryOptions(ThemeName.DarkPlus);
        return _options;
    }

    /// <summary>
    /// Guesses the TextMate scope name for <paramref name="remotePath"/> based on
    /// its file extension or well-known filename. Returns <see langword="null"/> when
    /// no grammar can be found.
    /// </summary>
    public static string? GuessScope(string remotePath)
    {
        var options = GetRegistryOptions();
        var ext = Path.GetExtension(remotePath).ToLowerInvariant();
        var fileName = Path.GetFileName(remotePath).ToLowerInvariant();

        // Extension-based lookup
        if (!string.IsNullOrEmpty(ext))
        {
            var lang = options.GetLanguageByExtension(ext);
            if (lang != null)
                return options.GetScopeByLanguageId(lang.Id);
        }

        // Extensionless well-known files
        return fileName switch
        {
            "dockerfile" or "containerfile" => TryScope(options, "dockerfile"),
            "makefile" or "gnumakefile"     => TryScope(options, "makefile"),
            ".bashrc" or ".bash_profile" or ".profile" or ".zshrc" or ".zprofile"
                or ".bash_aliases" => TryScope(options, "shellscript"),
            ".gitignore" or ".dockerignore" => TryScope(options, "ignore"),
            _ => null,
        };
    }

    private static string? TryScope(RegistryOptions options, string langId)
    {
        var scope = options.GetScopeByLanguageId(langId);
        return string.IsNullOrEmpty(scope) ? null : scope;
    }

    /// <summary>
    /// Returns all grammars available in TextMateSharp, sorted by display name.
    /// The first entry is always "Plain Text" (null scope).
    /// </summary>
    public static IReadOnlyList<SyntaxEntry> GetAll()
    {
        var options = GetRegistryOptions();
        var list = new List<SyntaxEntry> { new("Plain Text", null) };

        foreach (var lang in options.GetAvailableLanguages().OrderBy(l => l.Id))
        {
            var scope = options.GetScopeByLanguageId(lang.Id);
            if (string.IsNullOrEmpty(scope)) continue;
            var display = lang.Aliases?.FirstOrDefault() ?? lang.Id;
            list.Add(new SyntaxEntry(display, scope));
        }

        return list;
    }
}

/// <summary>Represents one entry in the syntax-highlighting dropdown.</summary>
internal sealed record SyntaxEntry(string DisplayName, string? ScopeName)
{
    public override string ToString() => DisplayName;
}
