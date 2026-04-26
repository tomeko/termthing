using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

public partial class SerialConnectDialog : Window
{
    public SerialConnectDialog()
    {
        InitializeComponent();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
