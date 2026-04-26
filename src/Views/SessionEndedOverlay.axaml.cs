using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

public partial class SessionEndedOverlay : UserControl
{
    public event EventHandler? ReconnectRequested;
    public event EventHandler? CloseRequested;

    public SessionEndedOverlay(string? reason = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(reason))
        {
            ReasonText.Text = reason;
            ReasonText.IsVisible = true;
        }

        // Default focus to Close button so Enter/Space close the tab.
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void OnReconnectClicked(object? sender, RoutedEventArgs e)
        => ReconnectRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);
}
