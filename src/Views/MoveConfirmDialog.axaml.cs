using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

public partial class MoveConfirmDialog : Window
{
    public MoveConfirmDialog(int itemCount, string destDir)
    {
        InitializeComponent();
        MessageText.Text = itemCount == 1
            ? "Move 1 item to:"
            : $"Move {itemCount} items to:";
        DestText.Text = destDir;
    }

    private void OnMoveClicked(object? sender, RoutedEventArgs e)   => Close(true);
    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
