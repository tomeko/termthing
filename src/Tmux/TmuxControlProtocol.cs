using System.Globalization;
using System.Text;

namespace TermThing.Tmux;

/// <summary>
/// A tmux control-mode notification (a <c>%name …</c> line outside a reply block),
/// other than <c>%output</c>, which is decoded separately as bytes.
/// </summary>
/// <param name="Name">Notification name without the <c>%</c>, e.g. <c>layout-change</c>.</param>
/// <param name="Args">The rest of the line, split on spaces.</param>
/// <param name="Rest">The rest of the line as one string (for arguments that may contain spaces, like window names).</param>
public sealed record TmuxNotification(string Name, string[] Args, string Rest)
{
    /// <summary>Argument <paramref name="i"/>, or the empty string.</summary>
    public string Arg(int i) => i < Args.Length ? Args[i] : string.Empty;

    /// <summary>The text after the first <paramref name="skip"/> arguments (e.g. a window name after its id).</summary>
    public string RestAfter(int skip)
    {
        int pos = 0;
        for (int i = 0; i < skip; i++)
        {
            int space = Rest.IndexOf(' ', pos);
            if (space < 0) return string.Empty;
            pos = space + 1;
        }
        return Rest[pos..];
    }
}

/// <summary>The reply to one command: the lines between <c>%begin</c> and <c>%end</c>/<c>%error</c>.</summary>
public sealed record TmuxReply(bool Success, IReadOnlyList<string> Lines)
{
    public string Text => string.Join("\n", Lines);
}

/// <summary>
/// Parsing helpers for tmux control mode (<c>tmux -C</c>). Lines are handled as bytes
/// because <c>%output</c> carries the pane's raw output: only bytes below 32 and the
/// backslash are octal-escaped, everything else (UTF-8 included) is passed through as is.
/// </summary>
public static class TmuxControlProtocol
{
    private static readonly byte[] OutputPrefix = "%output %"u8.ToArray();

    /// <summary>
    /// Recognises <c>%output %&lt;pane&gt; &lt;data&gt;</c> and decodes its data.
    /// Returns false for any other line.
    /// </summary>
    public static bool TryParseOutput(ReadOnlySpan<byte> line, out int paneId, out byte[] data)
    {
        paneId = -1;
        data = [];
        if (!line.StartsWith(OutputPrefix)) return false;
        var rest = line[OutputPrefix.Length..];
        int space = rest.IndexOf((byte)' ');
        if (space <= 0 || !TryParseInt(rest[..space], out paneId)) return false;
        data = DecodeOctal(rest[(space + 1)..]);
        return true;
    }

    /// <summary>Undoes tmux's <c>\ooo</c> escaping.</summary>
    public static byte[] DecodeOctal(ReadOnlySpan<byte> s)
    {
        var result = new byte[s.Length];
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            byte b = s[i];
            if (b == (byte)'\\' && IsOctal(s, i + 1))
            {
                result[n++] = (byte)(((s[i + 1] - '0') << 6) | ((s[i + 2] - '0') << 3) | (s[i + 3] - '0'));
                i += 3;
            }
            else
            {
                result[n++] = b;
            }
        }
        return result.AsSpan(0, n).ToArray();
    }

    private static bool IsOctal(ReadOnlySpan<byte> s, int at) =>
        at + 2 < s.Length
        && s[at] is >= (byte)'0' and <= (byte)'7'
        && s[at + 1] is >= (byte)'0' and <= (byte)'7'
        && s[at + 2] is >= (byte)'0' and <= (byte)'7';

    /// <summary>Splits a <c>%name args…</c> line. Returns null when the line isn't a notification.</summary>
    public static TmuxNotification? ParseNotification(string line)
    {
        if (line.Length < 2 || line[0] != '%') return null;
        int space = line.IndexOf(' ');
        var name = space < 0 ? line[1..] : line[1..space];
        var rest = space < 0 ? string.Empty : line[(space + 1)..];
        return new TmuxNotification(name, rest.Length == 0 ? [] : rest.Split(' '), rest);
    }

    /// <summary>
    /// Parses a <c>%begin</c>/<c>%end</c>/<c>%error</c> guard line: <c>%begin time number flags</c>.
    /// <paramref name="fromThisClient"/> is true for replies to commands we sent (flags = 1);
    /// the block for the attach command itself has flags 0.
    /// </summary>
    public static bool TryParseGuard(string line, out string kind, out long number, out bool fromThisClient)
    {
        kind = string.Empty;
        number = 0;
        fromThisClient = false;
        if (!(line.StartsWith("%begin ", StringComparison.Ordinal)
              || line.StartsWith("%end ", StringComparison.Ordinal)
              || line.StartsWith("%error ", StringComparison.Ordinal)))
            return false;
        var parts = line.Split(' ');
        if (parts.Length < 4) return false;
        kind = parts[0][1..];
        long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out number);
        fromThisClient = parts[3] == "1";
        return true;
    }

    /// <summary>Parses tmux ids such as <c>%5</c>, <c>@3</c> or <c>$1</c> (the sigil is optional).</summary>
    public static bool TryParseId(string s, out int id)
    {
        id = -1;
        if (s.Length > 0 && s[0] is '%' or '@' or '$') s = s[1..];
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    private static bool TryParseInt(ReadOnlySpan<byte> s, out int value)
    {
        value = 0;
        if (s.IsEmpty) return false;
        foreach (var b in s)
        {
            if (b is < (byte)'0' or > (byte)'9') return false;
            value = value * 10 + (b - '0');
        }
        return true;
    }

    /// <summary>
    /// Quotes an argument for the tmux command parser (which, in control mode, reads
    /// our commands directly; no shell is involved).
    /// </summary>
    public static string Quote(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var c in s)
        {
            if (c == '\'') sb.Append("'\\''");
            else sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}
