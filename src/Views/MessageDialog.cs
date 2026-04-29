using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TermThing.Views;

/// <summary>
/// Minimal modal "OK" alert dialog. Used for one-shot informational messages
/// such as "Docker is not installed" surfaced from background SSH probes.
/// </summary>
internal static class MessageDialog
{
    public static Task ShowAsync(Window? owner, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var okBtn = new Button
        {
            Content = "OK",
            IsDefault = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Avalonia.Thickness(18, 4),
        };

        var dialog = new Window
        {
            Title = title,
            Width = 380,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20, 16),
                Spacing = 14,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.White,
                    },
                    okBtn,
                },
            },
        };

        okBtn.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(true);

        if (owner != null)
            dialog.ShowDialog(owner);
        else
            dialog.Show();

        return tcs.Task;
    }
}
