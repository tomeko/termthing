using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System.Collections.ObjectModel;
using System.Linq;
using TermThing.Configuration;
using TermThing.Sessions;

namespace TermThing.Views;

// ---------------------------------------------------------------------------
// Simple view-model for a jump-host list item
// ---------------------------------------------------------------------------

internal sealed class JumpHostListItem(JumpHost hop, string displayLabel)
{
    public JumpHost   Hop          { get; } = hop;
    public string     DisplayLabel { get; } = displayLabel;
}

/// <summary>
/// Dual-mode dialog:
/// <list type="bullet">
///   <item><b>Connect mode</b> (default): shows password/passphrase fields; no save option.</item>
///   <item><b>Edit mode</b>: hides secret fields; used to create/edit a <see cref="SessionDefinition"/>.</item>
/// </list>
/// </summary>
public partial class SshConnectDialog : Window
{
    private readonly bool         _editMode;
    private readonly AppConfig?   _appConfig;

    // Session-name auto-fill state (connect mode only):
    // We track the last value we wrote ourselves; if the box holds a different
    // value the user must have typed something, so we stop auto-filling.
    private bool   _sessionNameTouched;
    private string _autoSessionName = string.Empty;

    // Jump-host list (edit mode only)
    private readonly ObservableCollection<JumpHostListItem> _jumpItems = [];

    // Outputs — credentials (connect mode)
    public string? Host            { get; private set; }
    public int     Port            { get; private set; } = 22;
    public string? Username        { get; private set; }
    public string? Password        { get; private set; }
    public string? KeyFile         { get; private set; }
    public bool    EnableSftp      { get; private set; }
    public bool    SaveAsSession   { get; private set; }
    public string? SessionName     { get; private set; }

    // Output — jump hosts (edit mode only)
    public IReadOnlyList<JumpHost> JumpHosts => [.. _jumpItems.Select(i => i.Hop)];

    public SshConnectDialog(bool editMode = false, SshSettings? prefill = null, AppConfig? appConfig = null)
    {
        _editMode  = editMode;
        _appConfig = appConfig;
        InitializeComponent();
        Opened += (_, _) => (editMode ? (Control)EditSessionNameTextBox : HostTextBox).Focus();

        // Edit mode: hide secret fields, show Name field, no "Save as session" row
        SecretFieldsPanel.IsVisible  = !editMode;
        SaveAsSessionRow.IsVisible   = false;   // placeholder row — always hidden
        SessionNameRow.IsVisible     = !editMode;
        SaveSessionToggle.IsVisible  = !editMode;
        NameRow.IsVisible            = editMode;
        JumpHostsSection.IsVisible   = true;

        JumpHostListBox.ItemsSource  = _jumpItems;

        // SFTP browser is on by default for new SSH sessions; prefill (if any)
        // overrides this immediately below.
        SftpCheckBox.IsChecked = true;

        if (prefill is not null)
        {
            HostTextBox.Text     = prefill.Host;
            PortTextBox.Text     = prefill.Port.ToString();
            UsernameTextBox.Text = prefill.Username;
            KeyFileTextBox.Text  = prefill.KeyFilePath;
            SftpCheckBox.IsChecked = prefill.EnableSftp;

            foreach (var hop in prefill.JumpHosts)
                _jumpItems.Add(MakeListItem(hop));
        }

        Title = editMode ? "Edit SSH Session" : "SSH Connection";
        ConnectButton.Content = editMode ? "Save" : "Connect";

        // Connect-mode only: live-update the Session name field with
        // "{user}@{host}" until the user types into it themselves.
        if (!editMode)
        {
            HostTextBox.TextChanged       += OnHostOrUsernameChanged;
            UsernameTextBox.TextChanged   += OnHostOrUsernameChanged;
            SessionNameTextBox.TextChanged += OnSessionNameChanged;
        }
    }

    private void OnHostOrUsernameChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        if (_sessionNameTouched) return;

        var host = HostTextBox.Text?.Trim() ?? string.Empty;
        var user = UsernameTextBox.Text?.Trim() ?? string.Empty;
        var auto = (host.Length == 0 && user.Length == 0)
            ? string.Empty
            : $"{user}@{host}";

        _autoSessionName = auto;
        SessionNameTextBox.Text = auto;
    }

    private void OnSessionNameChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        // If the box still holds what we last wrote, this event is our own update — ignore it.
        if (SessionNameTextBox.Text == _autoSessionName) return;
        _sessionNameTouched = true;
    }

    // -----------------------------------------------------------------------
    // Browse key
    // -----------------------------------------------------------------------

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

    // -----------------------------------------------------------------------
    // Save / Connect
    // -----------------------------------------------------------------------

    private void OnConnectClicked(object? sender, RoutedEventArgs e)
    {
        ErrorTextBlock.IsVisible = false;

        var host = HostTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host)) { ShowError("Host is required."); return; }

        if (!int.TryParse(PortTextBox.Text, out var port) || port <= 0 || port > 65535)
        { ShowError("Port must be a number between 1 and 65535."); return; }

        var username = UsernameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(username)) { ShowError("Username is required."); return; }

        Host       = host;
        Port       = port;
        Username   = username;
        KeyFile    = string.IsNullOrWhiteSpace(KeyFileTextBox.Text) ? null : KeyFileTextBox.Text.Trim();
        EnableSftp = SftpCheckBox.IsChecked == true;

        if (_editMode)
        {
            SessionName = EditSessionNameTextBox.Text?.Trim();
        }
        else
        {
            Password     = PasswordTextBox.Text;
            SaveAsSession = SaveSessionToggle.IsChecked == true;
            SessionName   = SessionNameTextBox.Text?.Trim();
            if (SaveAsSession && string.IsNullOrWhiteSpace(SessionName))
                SessionName = $"{username}@{host}";
        }

        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private void ShowError(string msg)
    {
        ErrorTextBlock.Text    = msg;
        ErrorTextBlock.IsVisible = true;
    }

    // -----------------------------------------------------------------------
    // Jump-host management (edit mode only)
    // -----------------------------------------------------------------------

    private async void OnAddSessionRefClicked(object? sender, RoutedEventArgs e)
    {
        var allSshSessions = CollectSshSessions(_appConfig?.RootGroup);
        if (allSshSessions.Count == 0)
        {
            ShowError("No saved SSH sessions available to reference as a jump host.");
            return;
        }

        var picker = new SessionPickerDialog(allSshSessions);
        var chosen = await picker.ShowDialog<SessionDefinition?>(this);
        if (chosen is null) return;

        var hop = new JumpHost
        {
            Kind      = JumpHostKind.SessionRef,
            SessionId = chosen.Id,
        };
        _jumpItems.Add(MakeListItem(hop, chosen));
    }

    private async void OnAddInlineHopClicked(object? sender, RoutedEventArgs e)
    {
        var hopDialog = new InlineJumpHopDialog();
        var result    = await hopDialog.ShowDialog<JumpHost?>(this);
        if (result is null) return;
        _jumpItems.Add(MakeListItem(result));
    }

    private void OnMoveHopUpClicked(object? sender, RoutedEventArgs e)
    {
        var idx = JumpHostListBox.SelectedIndex;
        if (idx <= 0) return;
        var item = _jumpItems[idx];
        _jumpItems.RemoveAt(idx);
        _jumpItems.Insert(idx - 1, item);
        JumpHostListBox.SelectedIndex = idx - 1;
    }

    private void OnMoveHopDownClicked(object? sender, RoutedEventArgs e)
    {
        var idx = JumpHostListBox.SelectedIndex;
        if (idx < 0 || idx >= _jumpItems.Count - 1) return;
        var item = _jumpItems[idx];
        _jumpItems.RemoveAt(idx);
        _jumpItems.Insert(idx + 1, item);
        JumpHostListBox.SelectedIndex = idx + 1;
    }

    private void OnRemoveHopClicked(object? sender, RoutedEventArgs e)
    {
        var idx = JumpHostListBox.SelectedIndex;
        if (idx < 0) return;
        _jumpItems.RemoveAt(idx);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private JumpHostListItem MakeListItem(JumpHost hop, SessionDefinition? refDef = null)
    {
        string label;
        if (hop.Kind == JumpHostKind.SessionRef)
        {
            var def = refDef ?? FindSession(hop.SessionId, _appConfig?.RootGroup);
            label = def is not null ? $"[session] {def.Name}" : $"[session] {hop.SessionId}";
        }
        else
        {
            label = string.IsNullOrWhiteSpace(hop.Username)
                ? $"{hop.Host}:{hop.Port}"
                : $"{hop.Username}@{hop.Host}:{hop.Port}";
        }
        return new JumpHostListItem(hop, label);
    }

    private static List<SessionDefinition> CollectSshSessions(SessionGroup? group)
    {
        if (group is null) return [];
        var result = new List<SessionDefinition>();
        CollectSshSessionsCore(group, result);
        return result;
    }

    private static void CollectSshSessionsCore(SessionGroup group, List<SessionDefinition> result)
    {
        foreach (var s in group.Sessions)
            if (s.Kind == SessionKind.Ssh) result.Add(s);
        foreach (var sub in group.Subgroups)
            CollectSshSessionsCore(sub, result);
    }

    private static SessionDefinition? FindSession(Guid? id, SessionGroup? group)
    {
        if (id is null || group is null) return null;
        foreach (var s in group.Sessions)
            if (s.Id == id) return s;
        foreach (var sub in group.Subgroups)
        {
            var found = FindSession(id, sub);
            if (found is not null) return found;
        }
        return null;
    }
}
