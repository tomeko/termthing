using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Linq;
using TermThing.Sessions;

namespace TermThing.Views;

/// <summary>
/// Dual-mode dialog:
/// <list type="bullet">
///   <item><b>Connect mode</b> (default): shows password/passphrase fields; no save option.</item>
///   <item><b>Edit mode</b>: hides secret fields; used to create/edit a <see cref="SessionDefinition"/>.</item>
/// </list>
/// </summary>
public partial class SshConnectDialog : Window
{
    private readonly bool _editMode;

    // Outputs — credentials (connect mode)
    public string? Host { get; private set; }
    public int Port { get; private set; } = 22;
    public string? Username { get; private set; }
    public string? Password { get; private set; }
    public string? KeyFile { get; private set; }
    public string? KeyPassphrase { get; private set; }
    public bool EnableSftp { get; private set; }
    public bool SaveAsSession { get; private set; }
    public string? SessionName { get; private set; }

    public SshConnectDialog(bool editMode = false, SshSettings? prefill = null)
    {
        _editMode = editMode;
        InitializeComponent();
        Opened += (_, _) => (editMode ? (Control)EditSessionNameTextBox : HostTextBox).Focus();

        // Edit mode: hide secret fields, show Name field, no "Save as session" row
        SecretFieldsPanel.IsVisible = !editMode;
        SaveAsSessionRow.IsVisible = !editMode;
        NameRow.IsVisible = editMode;

        if (prefill is not null)
        {
            HostTextBox.Text = prefill.Host;
            PortTextBox.Text = prefill.Port.ToString();
            UsernameTextBox.Text = prefill.Username;
            KeyFileTextBox.Text = prefill.KeyFilePath;
            SftpCheckBox.IsChecked = prefill.EnableSftp;
        }

        Title = editMode ? "Edit SSH Session" : "SSH Connection";
        ConnectButton.Content = editMode ? "Save" : "Connect";
    }

    private async void OnBrowseKeyClicked(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select private key file",
            AllowMultiple = false,
        });

        var picked = files?.FirstOrDefault();
        if (picked != null)
            KeyFileTextBox.Text = picked.TryGetLocalPath() ?? picked.Path.LocalPath;
    }

    private void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        ErrorTextBlock.IsVisible = false;

        var host = HostTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host)) { ShowError("Host is required."); return; }

        if (!int.TryParse(PortTextBox.Text, out var port) || port <= 0 || port > 65535)
        { ShowError("Port must be a number between 1 and 65535."); return; }

        var username = UsernameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(username)) { ShowError("Username is required."); return; }

        Host = host;
        Port = port;
        Username = username;
        KeyFile = string.IsNullOrWhiteSpace(KeyFileTextBox.Text) ? null : KeyFileTextBox.Text.Trim();
        EnableSftp = SftpCheckBox.IsChecked == true;

        if (_editMode)
        {
            SessionName = EditSessionNameTextBox.Text?.Trim();
        }
        else
        {
            Password = PasswordTextBox.Text;
            KeyPassphrase = KeyPassphraseTextBox.Text;
            SaveAsSession = SaveAsSessionCheckBox.IsChecked != true; // checkbox = "Temporary", so save = NOT checked
            SessionName = SessionNameTextBox.Text?.Trim();
            if (SaveAsSession && string.IsNullOrWhiteSpace(SessionName))
                SessionName = $"{username}@{host}";
        }

        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string msg)
    {
        ErrorTextBlock.Text = msg;
        ErrorTextBlock.IsVisible = true;
    }
}
