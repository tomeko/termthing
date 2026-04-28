using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

/// <summary>
/// Result of the save-confirmation dialog.
/// </summary>
internal sealed record SaveConfirmResult(bool Upload, bool Remember);

/// <summary>
/// Modal dialog asking the user whether to upload local changes back to the
/// remote file before closing or saving from the text editor.
/// </summary>
public partial class SaveConfirmDialog : Window
{
    private TextBlock _messageText = null!;
    private CheckBox _rememberCheck = null!;

    public SaveConfirmDialog(string remotePath, string displayHost)
    {
        InitializeComponent();
        _messageText  = this.FindControl<TextBlock>("MessageText")!;
        _rememberCheck = this.FindControl<CheckBox>("RememberCheck")!;

        _messageText.Text =
            $"Upload changes to\n{remotePath}\non {displayHost}?\n\n" +
            "This will overwrite the source file.";
    }

    private void OnUploadClicked(object? sender, RoutedEventArgs e) =>
        Close(new SaveConfirmResult(Upload: true, Remember: _rememberCheck.IsChecked == true));

    private void OnCancelClicked(object? sender, RoutedEventArgs e) =>
        Close(new SaveConfirmResult(Upload: false, Remember: false));
}
