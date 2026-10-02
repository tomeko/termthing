using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TermThing.Tmux;

/// <summary>
/// Turns what a pane's terminal sends (keys, pastes, mouse reports, and its own
/// replies to queries) into tmux <c>send-keys</c> commands.
/// </summary>
public static partial class TmuxInput
{
    private const int BytesPerCommand = 1024;

    /// <summary>
    /// True for the terminal's automatic replies to queries the program in the pane
    /// sent. tmux sees those queries first and answers them itself (checked against
    /// tmux 3.4: DA1, DA2, XTVERSION, cursor position, status, window size and OSC
    /// colour queries), so passing ours on as keys would type a second reply into the
    /// program. Mode reports (DECRPM, <c>CSI ? … $ y</c>) are the exception: tmux leaves
    /// those unanswered, so ours go through.
    /// <para>
    /// Every write from the terminal is one whole sequence (a key, a reply or a paste),
    /// so each is classified on its own. A key never looks like a reply, except that
    /// Shift/Ctrl+F3 (<c>CSI 1 ; m R</c>) has the same shape as a cursor-position reply
    /// for row 1; those are kept as keys.
    /// </para>
    /// </summary>
    public static bool IsTerminalReply(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3 || data[0] != 0x1b) return false;
        var s = Encoding.Latin1.GetString(data);
        return ReplyPattern().IsMatch(s) && !ModifiedF3().IsMatch(s);
    }

    /// <summary>
    /// <c>send-keys -H</c> commands that deliver <paramref name="data"/> to pane
    /// <paramref name="paneId"/> byte for byte (UTF-8, escape sequences and control
    /// characters included). Long input is split over several commands.
    /// </summary>
    public static IEnumerable<string> SendKeys(int paneId, ReadOnlyMemory<byte> data)
    {
        for (int at = 0; at < data.Length; at += BytesPerCommand)
        {
            var chunk = data.Span.Slice(at, Math.Min(BytesPerCommand, data.Length - at));
            var sb = new StringBuilder(24 + chunk.Length * 3);
            sb.Append(CultureInfo.InvariantCulture, $"send-keys -t %{paneId} -H");
            foreach (var b in chunk) sb.Append(' ').Append(b.ToString("x2", CultureInfo.InvariantCulture));
            yield return sb.ToString();
        }
    }

    /// <summary>
    /// Commands that paste <paramref name="text"/> into pane <paramref name="paneId"/>
    /// through a tmux buffer. <c>paste-buffer -p</c> adds bracketed-paste markers only
    /// when the program in the pane asked for them, which tmux knows and this side may
    /// not (tmux 3.4 has no format for it, so a program that turned it on before we
    /// attached is invisible to us). Newlines become CRs, as in a terminal paste.
    /// </summary>
    public static IEnumerable<string> Paste(int paneId, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        if (bytes.Length == 0) yield break;
        bool first = true;
        for (int at = 0; at < bytes.Length;)
        {
            int end = Math.Min(bytes.Length, at + BytesPerCommand);
            // Don't split a UTF-8 sequence over two commands.
            while (end < bytes.Length && end > at + 1 && (bytes[end] & 0xC0) == 0x80) end--;
            var sb = new StringBuilder(end - at + 48);
            sb.Append(first ? "set-buffer" : "set-buffer -a").Append(" -b ").Append(PasteBuffer).Append(" -- \"");
            for (int i = at; i < end; i++)
            {
                byte b = bytes[i];
                // tmux expands $ and ~ and handles \ escapes inside double quotes; an octal
                // escape is the one form that always means "this byte".
                if (b < 0x20 || b == 0x7f || b is (byte)'\\' or (byte)'"' or (byte)'$' or (byte)'~')
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else
                    sb.Append((char)b);
            }
            // Raw UTF-8 above stays as bytes: decode the line back the way it is sent.
            yield return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(sb.Append('"').ToString()));
            first = false;
            at = end;
        }
        yield return $"paste-buffer -p -d -b {PasteBuffer} -t %{paneId}";
    }

    private const string PasteBuffer = "termthing-paste";

    // DA1/DA2/kitty-keyboard/XTSMGRAPHICS replies; CPR/DECXCPR; DSR; window reports;
    // DECREQTPARM; DECRQDE; DECSLE-style; focus in/out; DCS replies (DA3, XTVERSION,
    // DECRQSS, XTGETTCAP); OSC replies (colours, clipboard).
    [GeneratedRegex(
        @"^\x1b\[(?:[?>][\d;]*[cuS]|\??\d+;\d+(?:;\d+)?R|\??[\d;]*n|[\d;]*[tx]|[\d;]*""w|[\d;]*\*\{|[IO])$" +
        @"|^\x1bP[\s\S]*\x1b\\$" +
        @"|^\x1b\][\s\S]*(?:\x07|\x1b\\)$")]
    private static partial Regex ReplyPattern();

    [GeneratedRegex(@"^\x1b\[1;\d+R$")]
    private static partial Regex ModifiedF3();
}
