using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace TermThing.Views;

/// <summary>
/// Minimal read-only markdown renderer for GitHub release notes. Covers what release
/// notes actually use — headings, paragraphs, (nested) bullet and numbered lists,
/// block quotes, fenced code, rules, and inline bold / italic / strikethrough /
/// code / links / bare URLs. Anything else falls through as plain text. Kept
/// in-house rather than pulling in a markdown package that has to track Avalonia
/// major versions.
/// </summary>
internal sealed class MarkdownView : StackPanel
{
    private static readonly FontFamily MonoFont = new("Cascadia Code,Consolas,monospace");
    private static readonly IBrush TextBrush    = new SolidColorBrush(Color.Parse("#DDDDDD"));
    private static readonly IBrush DimBrush     = new SolidColorBrush(Color.Parse("#999999"));
    private static readonly IBrush CodeBg       = new SolidColorBrush(Color.Parse("#2A2D2E"));
    private static readonly IBrush RuleBrush    = new SolidColorBrush(Color.Parse("#444444"));
    internal static readonly IBrush LinkBrush   = new SolidColorBrush(Color.Parse("#4FA8FF"));

    private static readonly Regex HtmlComment = new(@"<!--[\s\S]*?-->");
    private static readonly Regex Heading     = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$");
    private static readonly Regex Rule        = new(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$");
    private static readonly Regex ListItem    = new(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$");
    private static readonly Regex Fence       = new(@"^\s*(```|~~~)");
    private static readonly Regex Quote       = new(@"^\s*>\s?(.*)$");

    public MarkdownView()
    {
        Spacing = 6;
    }

    public string Markdown
    {
        set
        {
            Children.Clear();
            Render(value ?? string.Empty);
        }
    }

    private void Render(string markdown)
    {
        var lines = HtmlComment.Replace(markdown.Replace("\r\n", "\n"), string.Empty).Split('\n');
        var para  = new List<string>();

        void FlushParagraph()
        {
            if (para.Count == 0) return;
            Children.Add(Text(string.Join("\n", para)));
            para.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var fence = Fence.Match(line);
            if (fence.Success)
            {
                FlushParagraph();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(fence.Groups[1].Value); i++)
                    code.Add(lines[i]);
                Children.Add(CodeBlock(string.Join("\n", code)));
                continue;
            }

            var heading = Heading.Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                Children.Add(HeadingBlock(heading.Groups[1].Length, heading.Groups[2].Value));
                continue;
            }

            if (Rule.IsMatch(line))
            {
                FlushParagraph();
                Children.Add(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 4) });
                continue;
            }

            var item = ListItem.Match(line);
            if (item.Success)
            {
                FlushParagraph();
                var text = new StringBuilder(item.Groups[3].Value);
                // Lazy continuation: following non-blank lines that don't start a new block.
                while (i + 1 < lines.Length
                       && !string.IsNullOrWhiteSpace(lines[i + 1])
                       && !ListItem.IsMatch(lines[i + 1])
                       && !Heading.IsMatch(lines[i + 1])
                       && !Fence.IsMatch(lines[i + 1]))
                    text.Append('\n').Append(lines[++i].Trim());

                var level  = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                var marker = item.Groups[2].Value;
                Children.Add(ListBlock(level, char.IsDigit(marker[0]) ? marker : "•", text.ToString()));
                continue;
            }

            var quote = Quote.Match(line);
            if (quote.Success)
            {
                FlushParagraph();
                var text = new List<string> { quote.Groups[1].Value };
                while (i + 1 < lines.Length && Quote.Match(lines[i + 1]) is { Success: true } next)
                {
                    text.Add(next.Groups[1].Value);
                    i++;
                }
                Children.Add(new Border
                {
                    BorderBrush     = RuleBrush,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding         = new Thickness(10, 2, 0, 2),
                    Child           = Text(string.Join("\n", text), DimBrush),
                });
                continue;
            }

            para.Add(line.Trim());
        }

        FlushParagraph();
    }

    private static LinkTextBlock Text(string markdown, IBrush? foreground = null)
    {
        var tb = new LinkTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground   = foreground ?? TextBrush,
        };
        tb.SetMarkdown(markdown);
        return tb;
    }

    private static Control HeadingBlock(int level, string markdown)
    {
        var tb = Text(markdown);
        tb.FontWeight = FontWeight.SemiBold;
        tb.FontSize   = level switch { 1 => 20, 2 => 17, 3 => 15, _ => 13 };
        tb.Margin     = new Thickness(0, level <= 2 ? 8 : 4, 0, 0);
        return tb;
    }

    private static Control ListBlock(int level, string marker, string markdown)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin            = new Thickness(4 + level * 16, 0, 0, 0),
        };
        var bullet = new TextBlock
        {
            Text       = marker,
            Foreground = DimBrush,
            Margin     = new Thickness(0, 0, 8, 0),
            MinWidth   = 10,
        };
        var text = Text(markdown);
        Grid.SetColumn(text, 1);
        grid.Children.Add(bullet);
        grid.Children.Add(text);
        return grid;
    }

    private static Control CodeBlock(string code) => new Border
    {
        Background   = CodeBg,
        CornerRadius = new CornerRadius(4),
        Padding      = new Thickness(10, 8),
        Child = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock
            {
                Text       = code,
                FontFamily = MonoFont,
                FontSize   = 12,
                Foreground = TextBrush,
            },
        },
    };

    // -----------------------------------------------------------------------
    // Inline text with clickable links
    // -----------------------------------------------------------------------

    /// <summary>
    /// A wrapping <see cref="TextBlock"/> built from flat <see cref="Run"/>s, with link
    /// ranges hit-tested on the text layout (so links wrap with the text instead of
    /// being embedded controls). Links open in the system browser.
    /// </summary>
    internal sealed class LinkTextBlock : TextBlock
    {
        private readonly List<(int Start, int End, Uri Url)> _links = [];

        public void SetMarkdown(string markdown)
        {
            Inlines ??= [];
            Inlines.Clear();
            _links.Clear();

            var pos = 0;
            foreach (var seg in InlineParser.Parse(markdown))
            {
                var run = new Run(seg.Text);
                if (seg.Bold)   run.FontWeight = FontWeight.SemiBold;
                if (seg.Italic) run.FontStyle  = FontStyle.Italic;
                if (seg.Strike) run.TextDecorations = Avalonia.Media.TextDecorations.Strikethrough;
                if (seg.Code)
                {
                    run.FontFamily = MonoFont;
                    run.Background = CodeBg;
                }
                if (seg.Url is not null)
                {
                    run.Foreground = LinkBrush;
                    _links.Add((pos, pos + seg.Text.Length, seg.Url));
                }
                Inlines.Add(run);
                pos += seg.Text.Length;
            }
        }

        private Uri? LinkAt(Point p)
        {
            if (_links.Count == 0) return null;
            var hit = TextLayout.HitTestPoint(new Point(p.X - Padding.Left, p.Y - Padding.Top));
            if (!hit.IsInside) return null;
            foreach (var (start, end, url) in _links)
                if (hit.TextPosition >= start && hit.TextPosition < end)
                    return url;
            return null;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var url = LinkAt(e.GetPosition(this));
            Cursor = url is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, url?.ToString());
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            if (LinkAt(e.GetPosition(this)) is { } url)
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(url);
        }
    }

    internal readonly record struct Segment(
        string Text, bool Bold = false, bool Italic = false, bool Strike = false,
        bool Code = false, Uri? Url = null);

    /// <summary>Recursive-descent parser for the inline subset.</summary>
    internal static class InlineParser
    {
        private static readonly Regex LinkRx    = new(@"\G\[([^\]]+)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)");
        private static readonly Regex AutoRx    = new(@"\G<(https?://[^>\s]+)>");
        private static readonly Regex BareUrlRx = new(@"\Ghttps?://[^\s<>()\[\]]+");

        public static List<Segment> Parse(string s)
        {
            var output = new List<Segment>();
            Parse(s, new Segment(string.Empty), output);
            return output;
        }

        private static void Parse(string s, Segment style, List<Segment> output)
        {
            var text = new StringBuilder();

            void Flush()
            {
                if (text.Length == 0) return;
                output.Add(style with { Text = text.ToString() });
                text.Clear();
            }

            for (var i = 0; i < s.Length;)
            {
                var c = s[i];

                // Backslash escape of markdown punctuation.
                if (c == '\\' && i + 1 < s.Length && (char.IsPunctuation(s[i + 1]) || char.IsSymbol(s[i + 1])))
                {
                    text.Append(s[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '`')
                {
                    var close = s.IndexOf('`', i + 1);
                    if (close > i)
                    {
                        Flush();
                        output.Add(style with { Text = s[(i + 1)..close], Code = true });
                        i = close + 1;
                        continue;
                    }
                }

                if (c == '[' && style.Url is null && LinkRx.Match(s, i) is { Success: true } link
                    && Uri.TryCreate(link.Groups[2].Value, UriKind.Absolute, out var linkUri) && IsWeb(linkUri))
                {
                    Flush();
                    Parse(link.Groups[1].Value, style with { Url = linkUri }, output);
                    i += link.Length;
                    continue;
                }

                if (c == '<' && style.Url is null && AutoRx.Match(s, i) is { Success: true } auto
                    && Uri.TryCreate(auto.Groups[1].Value, UriKind.Absolute, out var autoUri))
                {
                    Flush();
                    output.Add(style with { Text = auto.Groups[1].Value, Url = autoUri });
                    i += auto.Length;
                    continue;
                }

                if (c == 'h' && style.Url is null && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))
                    && BareUrlRx.Match(s, i) is { Success: true } bare)
                {
                    var url = bare.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
                    if (Uri.TryCreate(url, UriKind.Absolute, out var bareUri))
                    {
                        Flush();
                        output.Add(style with { Text = PrettyUrl(bareUri, url), Url = bareUri });
                        i += url.Length;
                        continue;
                    }
                }

                if (TryDelimited(s, i, "**", out var inner, out var len) ||
                    TryDelimited(s, i, "__", out inner, out len))
                {
                    Flush();
                    Parse(inner, style with { Bold = true }, output);
                    i += len;
                    continue;
                }

                if (TryDelimited(s, i, "~~", out inner, out len))
                {
                    Flush();
                    Parse(inner, style with { Strike = true }, output);
                    i += len;
                    continue;
                }

                if (TryDelimited(s, i, "*", out inner, out len) ||
                    TryDelimited(s, i, "_", out inner, out len))
                {
                    Flush();
                    Parse(inner, style with { Italic = true }, output);
                    i += len;
                    continue;
                }

                text.Append(c);
                i++;
            }

            Flush();
        }

        /// <summary>
        /// Matches <paramref name="delim"/>inner<paramref name="delim"/> at <paramref name="i"/>.
        /// Opening delimiters must be followed by non-space. Underscores and single '*'
        /// can't open mid-word, so snake_case and arithmetic like 2*3 stay intact.
        /// </summary>
        private static bool TryDelimited(string s, int i, string delim, out string inner, out int length)
        {
            inner = string.Empty;
            length = 0;

            if (string.CompareOrdinal(s, i, delim, 0, delim.Length) != 0) return false;
            var start = i + delim.Length;
            if (start >= s.Length || char.IsWhiteSpace(s[start])) return false;
            if ((delim.Length == 1 || delim[0] == '_') && i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;

            for (var close = s.IndexOf(delim, start + 1, StringComparison.Ordinal);
                 close > start;
                 close = s.IndexOf(delim, close + 1, StringComparison.Ordinal))
            {
                if (char.IsWhiteSpace(s[close - 1])) continue;
                var after = close + delim.Length;
                if (delim[0] == '_' && after < s.Length && char.IsLetterOrDigit(s[after])) continue;
                // A single '*' must not match the first half of a '**'.
                if (delim == "*" && after < s.Length && s[after] == '*') continue;

                inner  = s[start..close];
                length = after - i;
                return true;
            }
            return false;
        }

        private static bool IsWeb(Uri uri) => uri.Scheme is "http" or "https";

        /// <summary>
        /// Shortens GitHub's auto-generated PR / issue / compare URLs the way github.com
        /// renders them (<c>#12</c>, <c>v0.1.0...v0.2.0</c>); other URLs are shown as-is.
        /// </summary>
        private static string PrettyUrl(Uri uri, string original)
        {
            if (uri.Host != "github.com") return original;
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 4 && parts[2] is "pull" or "issues") return "#" + parts[3];
            if (parts.Length == 4 && parts[2] == "compare") return parts[3];
            return original;
        }
    }
}
