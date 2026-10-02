using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using TermThing.Configuration;
using TermThing.Updater;

namespace TermThing.Views;

/// <summary>
/// Version / build / environment info plus the running version's release notes,
/// fetched from GitHub Releases (the app's only changelog).
/// </summary>
public partial class AboutDialog : Window
{
    private readonly List<(string Label, string Value)> _info = [];

    public AboutDialog()
    {
        InitializeComponent();

        var version = VersionHelper.CurrentVersion();
        NameText.Text = $"TermThing {VersionHelper.DisplayVersion()}";

        _info.Add(("Version", version is null ? "development build" : VersionHelper.Format(version)));
        if (VersionHelper.CommitSha() is { } sha)
            _info.Add(("Commit", sha));
        _info.Add((".NET", RuntimeInformation.FrameworkDescription));
        if (typeof(Application).Assembly.GetName().Version is { } avalonia)
            _info.Add(("Avalonia", VersionHelper.Format(avalonia)));
        _info.Add(("OS", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})"));
        _info.Add(("Config", AppPaths.ConfigDirectory));

        for (var i = 0; i < _info.Count; i++)
        {
            InfoGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock
            {
                Text       = _info[i].Label,
                Foreground = new SolidColorBrush(Color.Parse("#999999")),
                Margin     = new Thickness(0, 1, 16, 1),
            };
            var value = new SelectableTextBlock
            {
                Text         = _info[i].Value,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 1),
            };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            InfoGrid.Children.Add(label);
            InfoGrid.Children.Add(value);
        }

        if (version is null)
        {
            NotesHeader.Text = "Release notes";
            ReleaseNotes.ShowMessage("Release notes are only available for published versions.");
        }
        else
        {
            NotesHeader.Text = $"What's new in {VersionHelper.Format(version)}";
            ReleaseNotes.ShowMessage("Loading release notes…");
            _ = LoadNotesAsync(version);
        }
    }

    private async Task LoadNotesAsync(Version version)
    {
        var notes = await Task.Run(() => UpdateService.GetNotesAsync(null, version));

        if (notes is null)
            ReleaseNotes.ShowMessage("Couldn't reach GitHub. Use \"All releases\" to read the notes in your browser.");
        else if (notes.Count == 0)
            ReleaseNotes.ShowMessage("No release notes found for this version on GitHub.");
        else
            ReleaseNotes.SetNotes(notes);
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard) return;

        var sb = new StringBuilder();
        // The config path is left out: it usually contains the user's name and
        // this text is meant for pasting into public bug reports.
        foreach (var (label, value) in _info.Where(i => i.Label != "Config"))
            sb.Append(label).Append(": ").AppendLine(value);
        await clipboard.SetTextAsync(sb.ToString());

        CopyButton.Content = "Copied";
    }

    private void OnRepoClicked(object? sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(UpdateService.RepoUrl);

    private void OnReleasesClicked(object? sender, RoutedEventArgs e) =>
        _ = Launcher.LaunchUriAsync(UpdateService.ReleasesUrl);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
