using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

/// <summary>
/// Result returned from <see cref="NewlinePasteConfirmDialog"/>. Null = user cancelled.
/// </summary>
public sealed record NewlinePasteConfirmResult(bool SkipForSession, bool SkipGlobally);

/// <summary>
/// Stronger paste-confirmation dialog shown when the clipboard contains a
/// newline character. A newline turns the paste into a shell command execution,
/// which is the highest-risk case — so it gets its own dedicated dialog with
/// its own remember-this-choice flags (independent from the regular paste
/// confirmation in <see cref="PasteConfirmDialog"/>).
/// </summary>
public partial class NewlinePasteConfirmDialog : Window
{
    private const int PreviewMaxChars = 2048;
    private const int PreviewMaxLines = 20;

    public NewlinePasteConfirmDialog(string clipboardText)
    {
        InitializeComponent();

        var (preview, truncated) = BuildPreview(clipboardText);
        PreviewText.Text = preview;

        var lineCount = CountLines(clipboardText);
        var size = clipboardText.Length;
        SummaryText.Text = truncated
            ? $"{size:N0} chars, {lineCount} line(s) — preview truncated."
            : $"{size:N0} chars, {lineCount} line(s).";
    }

    private static (string preview, bool truncated) BuildPreview(string text)
    {
        bool truncated = false;
        var t = text;
        if (t.Length > PreviewMaxChars)
        {
            t = t.Substring(0, PreviewMaxChars);
            truncated = true;
        }
        var lines = t.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > PreviewMaxLines)
        {
            t = string.Join('\n', lines[..PreviewMaxLines]);
            truncated = true;
        }
        return (t, truncated);
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 1;
        foreach (var c in text) if (c == '\n') n++;
        return n;
    }

    private void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        Close(new NewlinePasteConfirmResult(
            SkipForSession: SkipForSessionCheckBox.IsChecked == true,
            SkipGlobally:   SkipGloballyCheckBox.IsChecked == true));
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
