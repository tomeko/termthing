using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

public partial class RenameDialog : Window
{
    public RenameDialog(string currentName)
    {
        InitializeComponent();
        NameTextBox.Text = currentName;
        Opened += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(name))
            Close(name);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}
