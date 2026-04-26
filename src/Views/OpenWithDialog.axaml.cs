using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TermThing.Views;

public enum OpenWithResult
{
    None,
    SetNew,
    UseOs,
}

public partial class OpenWithDialog : Window
{
    private TextBlock _fileNameText = null!;

    public OpenWithResult Result { get; private set; } = OpenWithResult.None;

    public OpenWithDialog(string fileName)
    {
        InitializeComponent();

        _fileNameText = this.FindControl<TextBlock>("FileNameText")!;
        _fileNameText.Text = $"File: {fileName}";
    }

    private void OnSetNewClicked(object? sender, RoutedEventArgs e)
    {
        Result = OpenWithResult.SetNew;
        Close();
    }

    private void OnOsClicked(object? sender, RoutedEventArgs e)
    {
        Result = OpenWithResult.UseOs;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();
}
