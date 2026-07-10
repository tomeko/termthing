using Avalonia.Controls;
using Avalonia.Input;
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
        // Focus the default button (with keyboard navigation so the focus ring
        // shows) so Enter works and there's a clear starting point for the user.
        dialog.Opened += (_, _) => okBtn.Focus(NavigationMethod.Tab);

        if (owner != null)
            dialog.ShowDialog(owner);
        else
            dialog.Show();

        return tcs.Task;
    }

    /// <summary>
    /// Shows a modal OK / Cancel confirmation dialog.
    /// OK is the default button (activated by Enter). Returns <see langword="true"/> if OK was pressed.
    /// </summary>
    public static Task<bool> ShowConfirmAsync(Window? owner, string title, string message)
    {
        var tcs = new TaskCompletionSource<bool>();

        var okBtn = new Button
        {
            Content = "OK",
            IsDefault = true,
            Padding = new Avalonia.Thickness(18, 4),
        };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            IsCancel = true,
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
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { okBtn, cancelBtn },
                    },
                },
            },
        };

        okBtn.Click    += (_, _) => dialog.Close(true);
        cancelBtn.Click += (_, _) => dialog.Close(false);
        dialog.Closed  += (_, _) => tcs.TrySetResult(false); // fallback if closed via X
        dialog.Opened  += (_, _) => okBtn.Focus(NavigationMethod.Tab);

        if (owner != null)
            _ = dialog.ShowDialog<bool>(owner).ContinueWith(t => tcs.TrySetResult(t.Result), TaskScheduler.Default);
        else
            dialog.Show();

        return tcs.Task;
    }
}
