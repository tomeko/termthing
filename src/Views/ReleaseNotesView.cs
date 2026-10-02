using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TermThing.Updater;

namespace TermThing.Views;

/// <summary>
/// Renders one or more releases' notes, newest first. With several releases each
/// gets a version / date header so users who skipped versions can tell them apart.
/// Shared by the update, "What's new" and About dialogs.
/// </summary>
internal sealed class ReleaseNotesView : StackPanel
{
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.Parse("#999999"));

    public ReleaseNotesView()
    {
        Spacing = 12;
    }

    public void ShowMessage(string message)
    {
        Children.Clear();
        Children.Add(new TextBlock
        {
            Text         = message,
            Foreground   = DimBrush,
            TextWrapping = TextWrapping.Wrap,
        });
    }

    public void SetNotes(IReadOnlyList<ReleaseNote> notes)
    {
        Children.Clear();

        if (notes.Count == 0)
        {
            ShowMessage("(No release notes provided.)");
            return;
        }

        var headers = notes.Count > 1;
        for (var i = 0; i < notes.Count; i++)
        {
            var note = notes[i];

            if (i > 0)
                Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#444444")) });

            if (headers)
            {
                var header = new MarkdownView.LinkTextBlock
                {
                    FontSize   = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White,
                };
                // Version header links to the release page on GitHub.
                header.SetMarkdown($"[{note.TagName}]({note.HtmlUrl})");
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
                row.Children.Add(header);
                if (note.PublishedAt is { } date)
                    row.Children.Add(new TextBlock
                    {
                        Text              = date.LocalDateTime.ToString("yyyy-MM-dd"),
                        Foreground        = DimBrush,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                        Margin            = new Thickness(0, 0, 0, 2),
                    });
                Children.Add(row);
            }

            if (string.IsNullOrWhiteSpace(note.Markdown))
                Children.Add(new TextBlock { Text = "(No release notes provided.)", Foreground = DimBrush });
            else
                Children.Add(new MarkdownView { Markdown = note.Markdown });
        }
    }
}
