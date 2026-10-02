using Avalonia;
using Avalonia.Controls;
using TermThing.Updater;

namespace TermThing.Views;

/// <summary>
/// Shown once on the first launch after an upgrade, with the notes for every
/// release since the previously run version.
/// </summary>
internal sealed class WhatsNewDialog : Window
{
    public WhatsNewDialog(IReadOnlyList<ReleaseNote> notes)
    {
        Title                 = "What's new";
        Width                 = 600;
        Height                = 520;
        MinWidth              = 400;
        MinHeight             = 300;
        CanResize             = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var notesView = new ReleaseNotesView { Margin = new Thickness(12, 10) };
        notesView.SetNotes(notes);

        var releasesBtn = new Button { Content = "All releases" };
        releasesBtn.Click += (_, _) => _ = Launcher.LaunchUriAsync(UpdateService.ReleasesUrl);

        var closeBtn = new Button { Content = "Close", IsDefault = true, IsCancel = true };
        closeBtn.Click += (_, _) => Close();

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(closeBtn, 2);
        buttons.Children.Add(releasesBtn);
        buttons.Children.Add(closeBtn);

        var notesBorder = new Border
        {
            BorderBrush     = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#444444")),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Margin          = new Thickness(0, 0, 0, 12),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = notesView,
            },
        };

        var headline = new TextBlock
        {
            Text       = $"TermThing has been updated to {VersionHelper.DisplayVersion()}.",
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize   = 14,
            Margin     = new Thickness(0, 0, 0, 8),
        };

        var root = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(notesBorder, 1);
        Grid.SetRow(buttons, 2);
        root.Children.Add(headline);
        root.Children.Add(notesBorder);
        root.Children.Add(buttons);
        Content = root;

        Opened += (_, _) => closeBtn.Focus();
    }
}
