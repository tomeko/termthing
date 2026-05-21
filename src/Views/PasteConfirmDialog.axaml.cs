using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

/// <summary>
/// Result returned from <see cref="PasteConfirmDialog"/>. Null result = user cancelled.
/// </summary>
public sealed record PasteConfirmResult(bool SkipForSession, bool SkipGlobally);

public partial class PasteConfirmDialog : Window
{
    private const int PreviewMaxChars = 1024;
    private const int PreviewMaxLines = 12;

    public PasteConfirmDialog(string clipboardText)
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
        Close(new PasteConfirmResult(
            SkipForSession: SkipForSessionCheckBox.IsChecked == true,
            SkipGlobally:   SkipGloballyCheckBox.IsChecked == true));
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
