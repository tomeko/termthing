using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using TermThing.Sessions;

namespace TermThing.Views;

/// <summary>
/// Small dialog for entering the connection details of an inline jump-host hop
/// (host, port, username, optional private key).
/// </summary>
public sealed class InlineJumpHopDialog : Window
{
    private readonly TextBox _hostBox;
    private readonly TextBox _portBox;
    private readonly TextBox _userBox;
    private readonly TextBox _keyBox;
    private readonly TextBlock _error;

    public InlineJumpHopDialog()
    {
        Title  = "Add inline jump hop";
        Width  = 420;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _hostBox  = new TextBox { Watermark = "e.g. bastion.example.com" };
        _portBox  = new TextBox { Text = "22", Width = 80 };
        _userBox  = new TextBox();
        _keyBox   = new TextBox { Watermark = "(optional)" };
        _error    = new TextBlock { Foreground = Avalonia.Media.Brushes.OrangeRed, IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var browseBtn = new Button { Content = "…", Margin = new Avalonia.Thickness(4, 0, 0, 0) };
        browseBtn.Click += OnBrowseKeyAsync;

        var ok     = new Button { Content = "Add",    IsDefault = true, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel  = true };
        ok.Click     += OnOkClicked;
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(16),
            Spacing = 6,
            Children =
            {
                MakeRow("Host:",            _hostBox),
                MakeRow("Port:",            _portBox),
                MakeRow("Username:",        _userBox),
                MakeRow("Private key:",     new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children          = { WithCol(_keyBox, 0), WithCol(browseBtn, 1) },
                }),
                _error,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel },
                },
            },
        };
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        var host = _hostBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            ShowError("Host is required."); return;
        }
        if (!int.TryParse(_portBox.Text, out var port) || port <= 0 || port > 65535)
        {
            ShowError("Port must be a number between 1 and 65535."); return;
        }
        var user = _userBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(user))
        {
            ShowError("Username is required."); return;
        }

        var hop = new JumpHost
        {
            Kind       = JumpHostKind.Inline,
            Host       = host,
            Port       = port,
            Username   = user,
            KeyFilePath = string.IsNullOrWhiteSpace(_keyBox.Text) ? null : _keyBox.Text.Trim(),
        };
        Close(hop);
    }

    private async void OnBrowseKeyAsync(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select private key file",
            AllowMultiple = false,
        });
        var picked = files?.FirstOrDefault();
        if (picked is not null)
            _keyBox.Text = picked.TryGetLocalPath() ?? picked.Path.LocalPath;
    }

    private void ShowError(string msg) { _error.Text = msg; _error.IsVisible = true; }

    private static Grid MakeRow(string label, Control control)
    {
        var lbl = new TextBlock
        {
            Text = label,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Width = 110,
        };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        g.Children.Add(lbl);
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static Control WithCol(Control c, int col) { Grid.SetColumn(c, col); return c; }
}
