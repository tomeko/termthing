using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TermThing.Views;

/// <summary>
/// Modal confirmation shown by <see cref="MainWindow"/> when the user closes the
/// application while session tabs (or unsaved SFTP editor windows) are open.
/// Returns <see langword="true"/> from <c>ShowDialog&lt;bool?&gt;</c> when the
/// user wants to exit; the <see cref="DontAskAgain"/> flag is consulted by the
/// caller to update <c>AppSettings.ConfirmExitWithOpenSessions</c>.
/// </summary>
internal sealed class ExitConfirmDialog : Window
{
    public bool DontAskAgain { get; private set; }

    public ExitConfirmDialog(int openSessionCount, IReadOnlyList<string> dirtyEditorTitles)
    {
        Title = "Exit TermThing?";
        Width = 460;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var headline = openSessionCount switch
        {
            0 => "Close the application?",
            1 => "There is 1 open session.",
            _ => $"There are {openSessionCount} open sessions.",
        };

        var children = new Avalonia.Controls.Controls
        {
            new TextBlock
            {
                Text = headline,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            },
            new TextBlock
            {
                Text = "All session connections will be closed.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 12,
            },
        };

        if (dirtyEditorTitles.Count > 0)
        {
            children.Add(new TextBlock
            {
                Text = dirtyEditorTitles.Count == 1
                    ? "⚠ One SFTP editor has unsaved changes:"
                    : $"⚠ {dirtyEditorTitles.Count} SFTP editors have unsaved changes:",
                Foreground = Brushes.OrangeRed,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Avalonia.Thickness(0, 4, 0, 0),
            });

            // List up to 6 dirty files; collapse the rest into a "+N more" line.
            const int maxShown = 6;
            for (var i = 0; i < Math.Min(maxShown, dirtyEditorTitles.Count); i++)
            {
                children.Add(new TextBlock
                {
                    Text = "  • " + dirtyEditorTitles[i],
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            if (dirtyEditorTitles.Count > maxShown)
            {
                children.Add(new TextBlock
                {
                    Text = $"  + {dirtyEditorTitles.Count - maxShown} more…",
                    FontSize = 12,
                    Foreground = Brushes.Gray,
                });
            }
        }

        var dontAskCheck = new CheckBox
        {
            Content = "Don't ask again",
            Margin  = new Avalonia.Thickness(0, 8, 0, 0),
        };
        children.Add(dontAskCheck);

        var exitBtn = new Button
        {
            Content   = "Exit",
            IsDefault = true,
            Padding   = new Avalonia.Thickness(18, 4),
        };
        var cancelBtn = new Button
        {
            Content  = "Cancel",
            IsCancel = true,
            Padding  = new Avalonia.Thickness(18, 4),
        };

        exitBtn.Click += (_, _) =>
        {
            DontAskAgain = dontAskCheck.IsChecked == true;
            Close(true);
        };
        cancelBtn.Click += (_, _) => Close(false);

        var buttonRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing             = 8,
            Margin              = new Avalonia.Thickness(0, 12, 0, 0),
            Children            = { exitBtn, cancelBtn },
        };
        children.Add(buttonRow);

        var panel = new StackPanel
        {
            Margin  = new Avalonia.Thickness(20),
            Spacing = 6,
        };
        foreach (var child in children) panel.Children.Add(child);
        Content = panel;
    }
}
