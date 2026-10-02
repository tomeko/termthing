using System.Globalization;
using System.Text;

namespace TermThing.Tmux;

/// <summary>
/// Rebuilds what a pane already shows when we attach: tmux only sends output that
/// happens after that, so without this, a busy pane stays blank until it redraws.
/// <para>
/// <see cref="Commands"/> asks tmux for the pane's modes and contents in one go (so
/// they agree with each other, and only output that races the request shows twice);
/// <see cref="Build"/> replays the replies as one stream: scrollback and screen (the
/// normal screen first, then the alternate screen on top when a full-screen program
/// is running), then the modes, then the cursor. Checked against tmux 3.4. What tmux
/// can't report (bracketed paste, cursor shape, focus events, the current pen) isn't
/// restored; bracketed paste is handled at paste time instead (<see cref="TmuxInput.Paste"/>).
/// </para>
/// </summary>
public sealed class TmuxPaneRestore
{
    /// <summary>The <c>display-message -p</c> format whose one line <see cref="Parse"/> reads. Title last: it may contain spaces.</summary>
    private const string StateFormat =
        "#{alternate_on} #{cursor_x} #{cursor_y} #{alternate_saved_x} #{alternate_saved_y} " +
        "#{cursor_flag} #{insert_flag} #{origin_flag} #{wrap_flag} #{keypad_cursor_flag} #{keypad_flag} " +
        "#{mouse_standard_flag} #{mouse_button_flag} #{mouse_all_flag} #{mouse_sgr_flag} #{mouse_utf8_flag} " +
        "#{scroll_region_upper} #{scroll_region_lower} #{pane_height} " +
        "#{?#{!=:#{pane_title},#{host}},#{pane_title},}";

    private const int FieldCount = 20;

    private bool _alternate, _cursorVisible, _insert, _origin, _wrap, _keypadCursor, _keypad;
    private bool _mouseStandard, _mouseButton, _mouseAll, _mouseSgr, _mouseUtf8;
    private int _cursorX, _cursorY, _savedX, _savedY, _regionTop, _regionBottom, _height;
    private string _title = string.Empty;

    private TmuxPaneRestore() { }

    /// <summary>
    /// The commands to send together; <see cref="Parse"/> takes the first reply and
    /// <see cref="Build"/> the others. <paramref name="maxScrollback"/> caps how much
    /// history is fetched (the terminal would drop the rest anyway).
    /// </summary>
    public static IReadOnlyList<string> Commands(int paneId, int maxScrollback)
    {
        // -e: colours and attributes; -J: join wrapped lines, so the terminal wraps them
        // itself (same width) and keeps them as one line for selection.
        var target = string.Create(CultureInfo.InvariantCulture, $"-t %{paneId}");
        return
        [
            $"display-message -p {target} '{StateFormat}'",
            // History and screen. (A history-only capture can't be asked for blindly:
            // with no history, -E -1 gives the top screen line.)
            string.Create(CultureInfo.InvariantCulture, $"capture-pane -p -e -J -S -{Math.Max(0, maxScrollback)} {target}"),
            // With the alternate screen on: the normal screen behind it (empty otherwise).
            $"capture-pane -p -e -J -a -q {target}",
            // The screen alone, to tell it from the history in the first capture.
            $"capture-pane -p -e -J {target}",
        ];
    }

    /// <summary>Reads the reply to the first of <see cref="Commands"/>; null if it isn't one.</summary>
    public static TmuxPaneRestore? Parse(string line)
    {
        var p = line.Split(' ', FieldCount);
        if (p.Length < FieldCount - 1) return null;
        var ints = new int[FieldCount - 1];
        for (int i = 0; i < ints.Length; i++)
        {
            // Unknown values (e.g. no saved cursor) come as empty or as UINT_MAX: treat as 0.
            if (p[i].Length == 0) continue;
            if (!long.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) return null;
            ints[i] = value is >= 0 and <= int.MaxValue ? (int)value : 0;
        }
        return new TmuxPaneRestore
        {
            _alternate = ints[0] == 1,
            _cursorX = ints[1],
            _cursorY = ints[2],
            _savedX = ints[3],
            _savedY = ints[4],
            _cursorVisible = ints[5] == 1,
            _insert = ints[6] == 1,
            _origin = ints[7] == 1,
            _wrap = ints[8] == 1,
            _keypadCursor = ints[9] == 1,
            _keypad = ints[10] == 1,
            _mouseStandard = ints[11] == 1,
            _mouseButton = ints[12] == 1,
            _mouseAll = ints[13] == 1,
            _mouseSgr = ints[14] == 1,
            _mouseUtf8 = ints[15] == 1,
            _regionTop = ints[16],
            _regionBottom = ints[17],
            _height = ints[18],
            _title = p.Length > FieldCount - 1 ? p[FieldCount - 1] : string.Empty,
        };
    }

    /// <summary>
    /// The bytes to feed the terminal ahead of live output, from the replies to the
    /// <c>capture-pane</c> commands of <see cref="Commands"/>, in order.
    /// </summary>
    /// <param name="redraw">
    /// The terminal already shows this pane, but has missed output (it was paused): leave
    /// its scrollback, reset what the missed output may have changed, push the stale
    /// screen up into the scrollback and draw the current one. Ask with no scrollback.
    /// </param>
    public byte[] Build(IReadOnlyList<string> all, IReadOnlyList<string> normalScreen, IReadOnlyList<string> screen, bool redraw = false)
    {
        var sb = new StringBuilder();
        if (redraw)
        {
            // Leave the alternate screen; default pen, scroll region, origin, wrap, insert,
            // keypad, mouse and cursor visibility; then scroll a whole screen out from the
            // bottom row and start at the top.
            sb.Append("\x1b[?1049l\x1b[0m\x1b[r\x1b[?6l\x1b[?7h\x1b[4l\x1b[?1l\x1b>")
              .Append("\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1005l\x1b[?1006l\x1b[?25h")
              .Append("\x1b[9999;1H").Append('\n', Math.Max(1, _height)).Append("\x1b[H");
        }

        // "all" is the history plus the screen (the alternate screen when it is on).
        var history = all.Take(Math.Max(0, all.Count - screen.Count));
        if (!_alternate)
        {
            AppendLines(sb, [.. history, .. screen]);
        }
        else
        {
            // The normal screen behind the alternate one comes on its own.
            AppendLines(sb, [.. history, .. normalScreen]);
            // 1049 saves the cursor on the way in; put it where the program left it.
            sb.Append("\x1b[0m").Append(Cup(_savedY + 1, _savedX + 1));
            sb.Append("\x1b[?1049h\x1b[H");
            AppendLines(sb, screen);
        }

        // The captures carry the pen from line to line; the program's own pen isn't known.
        sb.Append("\x1b[0m");
        if (_regionTop != 0 || (_height > 0 && _regionBottom != _height - 1))
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"\x1b[{_regionTop + 1};{_regionBottom + 1}r"));
        if (_origin) sb.Append("\x1b[?6h");
        if (!_wrap) sb.Append("\x1b[?7l");
        if (_insert) sb.Append("\x1b[4h");
        if (_keypadCursor) sb.Append("\x1b[?1h");
        if (_keypad) sb.Append("\x1b=");
        if (_mouseStandard) sb.Append("\x1b[?1000h");
        if (_mouseButton) sb.Append("\x1b[?1002h");
        if (_mouseAll) sb.Append("\x1b[?1003h");
        if (_mouseUtf8) sb.Append("\x1b[?1005h");
        if (_mouseSgr) sb.Append("\x1b[?1006h");
        // In origin mode the cursor position counts from the top of the scroll region.
        sb.Append(Cup((_origin ? _cursorY - _regionTop : _cursorY) + 1, _cursorX + 1));
        if (!_cursorVisible) sb.Append("\x1b[?25l");
        if (_title.Length > 0) sb.Append("\x1b]2;").Append(_title.Replace("\x07", "")).Append('\x07');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Writes lines top to bottom, with no newline after the last one, so the last line
    /// lands on the bottom row and everything above it in the scrollback.
    /// </summary>
    private static void AppendLines(StringBuilder sb, IReadOnlyList<string> lines)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append("\r\n");
            sb.Append(lines[i]);
        }
    }

    private static string Cup(int row, int col) =>
        string.Create(CultureInfo.InvariantCulture, $"\x1b[{Math.Max(1, row)};{Math.Max(1, col)}H");
}
