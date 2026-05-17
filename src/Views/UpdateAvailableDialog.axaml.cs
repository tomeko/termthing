using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TermThing.Configuration;
using TermThing.Updater;

namespace TermThing.Views;

public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateInfo _info;
    private readonly Action _shutdownCallback;
    private bool _installing;

    public UpdateAvailableDialog(UpdateInfo info, bool manualCheck, Action shutdownCallback)
    {
        InitializeComponent();

        _info             = info;
        _shutdownCallback = shutdownCallback;

        HeadlineText.Text  = $"TermThing {info.TagName} is available.";
        ReleaseNotes.Text  = string.IsNullOrWhiteSpace(info.ReleaseNotesMarkdown)
            ? "(No release notes provided.)"
            : info.ReleaseNotesMarkdown;
    }

    private async void OnInstallClicked(object? sender, RoutedEventArgs e)
    {
        if (_installing) return;
        _installing = true;

        InstallButton.IsEnabled  = false;
        InstallProgress.IsVisible = true;
        StatusText.IsVisible      = false;

        var progress = new Progress<double>(v =>
        {
            Dispatcher.UIThread.Post(() => InstallProgress.Value = v);
        });

        try
        {
            await UpdateInstaller.PrepareAndApplyAsync(
                _info,
                progress,
                shutdownCallback: _shutdownCallback);
            // If we reach here the app is shutting down — close the dialog too
            Close();
        }
        catch (Exception ex)
        {
            _installing              = false;
            InstallButton.IsEnabled  = true;
            InstallProgress.IsVisible = false;
            StatusText.Text          = $"Install failed: {ex.Message}";
            StatusText.IsVisible     = true;
        }
    }

    private void OnLaterClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnSkipClicked(object? sender, RoutedEventArgs e)
    {
        SettingsService.Temp.SkippedUpdateTag = _info.TagName;
        SettingsService.SaveTemp();
        Close();
    }
}
