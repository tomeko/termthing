using System.Text;

namespace TermThing.Views;

/// <summary>
/// How aggressively clipboard text is rewritten before being sent to the PTY.
/// </summary>
public enum PasteSanitizationMode
{
    /// <summary>
    /// Send the clipboard through untouched. Only use when something upstream
    /// has already normalized the text.
    /// </summary>
    None = 0,

    /// <summary>
    /// Repair transport-level damage only: line-ending forms are unified to LF,
    /// byte-order marks and zero-width/bidi characters are dropped, and control
    /// characters that the terminal would interpret as commands are removed.
    /// Every remaining visible character keeps its identity.
    /// </summary>
    Safe = 1,

    /// <summary>
    /// <see cref="Safe"/>, plus folding of the Unicode punctuation that word
    /// processors, wikis and web pages substitute for ASCII — smart quotes,
    /// dashes and exotic spaces — back to the ASCII a shell understands.
    /// This is the mode that makes a command copied out of a document run the
    /// way it looks. It is lossy for prose.
    /// </summary>
    AsciiPunctuation = 2,
}

/// <summary>
/// Normalizes clipboard text into something a POSIX shell reads the way the user
/// expects.
/// <para>
/// Pasting into a remote shell is the one place where text copied on a Windows
/// desktop is handed straight to a Linux program, and the two disagree about more
/// than line endings. Clipboard text that came from a browser, an editor, a chat
/// client or a Word document routinely carries CRLF pairs, a UTF-8 BOM, non-breaking
/// spaces, typographic quotes and en/em dashes. Every one of those renders as an
/// ordinary-looking character in the terminal, so the pasted command *looks* correct
/// and then fails with "command not found", an unterminated quote, or an
/// unrecognized option.
/// </para>
/// </summary>
public static class PasteSanitizer
{
    // Line breaks that are not CR/LF.
    private const char NextLine          = '\u0085';
    private const char LineSeparator     = '\u2028';
    private const char ParagraphSeparator= '\u2029';

    private const char Delete            = '\u007F';
    private const char C1Start           = '\u0080';
    private const char C1End             = '\u009F';

    /// <summary>
    /// Rewrites <paramref name="text"/> for transmission to a PTY.
    /// </summary>
    /// <param name="text">Raw clipboard text.</param>
    /// <param name="mode">How aggressively to rewrite. See <see cref="PasteSanitizationMode"/>.</param>
    /// <returns>
    /// The sanitized text. Newlines are always LF; the caller decides whether the
    /// final line is terminated and whether to wrap the result in bracketed paste.
    /// </returns>
    public static string Sanitize(string? text, PasteSanitizationMode mode)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        if (mode == PasteSanitizationMode.None)
            return text!;

        bool ascii = mode == PasteSanitizationMode.AsciiPunctuation;
        var sb = new StringBuilder(text!.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // ---- Line endings ------------------------------------------------
            // CRLF collapses to a single LF; a lone CR (classic-Mac line ending,
            // and what a half-converted file leaves behind) also becomes LF. Left
            // alone, a bare CR returns the cursor to column 0 without advancing,
            // so the shell sees one line where the user saw two.
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                sb.Append('\n');
                continue;
            }
            if (c == '\n')
            {
                sb.Append('\n');
                continue;
            }
            // Unicode's own line breaks. Nothing downstream treats these as
            // newlines, so they would otherwise land mid-command as garbage.
            if (c == NextLine || c == LineSeparator || c == ParagraphSeparator)
            {
                sb.Append('\n');
                continue;
            }

            // ---- Invisible characters ----------------------------------------
            // A BOM or zero-width character takes no space on screen but is a real
            // byte to the shell: a zero-width-joined "ls" is not `ls`. Dropped
            // wherever they occur, not just at the start, because clipboard text
            // assembled from HTML often carries them between words.
            if (IsZeroWidth(c))
                continue;

            // ---- Control characters ------------------------------------------
            // Tab survives — it is legitimate input. Everything else below 0x20,
            // plus DEL and the C1 range, is dropped: ESC in particular would let
            // clipboard content steer the terminal rather than be typed into it,
            // and NUL truncates the write.
            if (c == '\t')
            {
                sb.Append('\t');
                continue;
            }
            if (c < '\u0020' || c == Delete || (c >= C1Start && c <= C1End))
                continue;

            if (!ascii)
            {
                sb.Append(c);
                continue;
            }

            // ---- ASCII punctuation folding -----------------------------------
            var folded = FoldToAscii(c);
            if (folded is null)
                sb.Append(c);
            else
                sb.Append(folded);
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <see cref="Sanitize"/> would change <paramref name="text"/> — i.e. the
    /// clipboard holds something that would not survive the trip intact. Lets a host
    /// warn the user, or preview the real text, instead of silently rewriting a paste.
    /// </summary>
    public static bool NeedsSanitizing(string? text, PasteSanitizationMode mode)
        => !string.IsNullOrEmpty(text)
           && !string.Equals(text, Sanitize(text, mode), StringComparison.Ordinal);

    /// <summary>
    /// Zero-width and bidirectional formatting characters. The bidi controls are
    /// dropped as well as the zero-width ones: they are invisible and can make a
    /// pasted command display in a different order from the one it executes in
    /// (the "Trojan Source" trick).
    /// </summary>
    private static bool IsZeroWidth(char c) => c switch
    {
        '\uFEFF' => true,                        // BOM / zero-width no-break space
        '\u200B' or '\u200C' or '\u200D' => true, // ZW space / non-joiner / joiner
        '\u2060' => true,                        // word joiner
        '\u200E' or '\u200F' => true,            // LRM / RLM
        >= '\u202A' and <= '\u202E' => true,     // LRE RLE PDF LRO RLO
        >= '\u2066' and <= '\u2069' => true,     // LRI RLI FSI PDI
        _ => false,
    };

    /// <summary>
    /// Maps a Unicode punctuation character to its ASCII equivalent, or null when
    /// the character should be kept as-is.
    /// </summary>
    private static string? FoldToAscii(char c) => c switch
    {
        // Quotation marks. A smart quote does not quote: the shell treats U+201C as
        // an ordinary character, so `cd <curly>My Dir<curly>` never finds the
        // directory, and the quotes are echoed verbatim instead of grouping.
        '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u2032' => "'",
        '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' => "\"",

        // Dashes. Word and many wikis autocorrect a `--flag` into an en dash, which
        // is the most common way a copied command line silently stops parsing as an
        // option. An em dash stood in for two hyphens, so it expands back to two.
        '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2212' => "-",
        '\u2014' or '\u2015' => "--",

        // Spaces that are not U+0020. A non-breaking space is the usual souvenir of
        // a copy out of a browser or a PDF: it looks like a separator, but the shell
        // reads it as part of the adjacent word.
        '\u00A0' or '\u202F' or '\u205F' or '\u3000' => " ",
        >= '\u2000' and <= '\u200A' => " ",

        // Ellipsis, expanded so a truncated command reads as truncated rather than
        // as a single unknown character.
        '\u2026' => "...",

        _ => null,
    };
}
