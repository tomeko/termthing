using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TermThing.Configuration;

namespace TermThing.Views;

/// <summary>Editable proxy for <see cref="ApplicationEntry"/> used by the Applications DataGrid.</summary>
public sealed class ApplicationEntryRow : INotifyPropertyChanged
{
    private bool _isDefault;

    public Guid   Id           { get; init; } = Guid.NewGuid();
    public string Name         { get; set; } = string.Empty;
    public string AppPath      { get; set; } = string.Empty;
    public string Args         { get; set; } = string.Empty;
    public string ExtensionsRaw { get; set; } = string.Empty;

    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ApplicationEntry ToEntry() => new()
    {
        Id         = Id,
        Name       = Name.Trim(),
        Kind       = ApplicationKind.External,
        AppPath    = AppPath.Trim(),
        Args       = Args.Trim(),
        Extensions = ExtensionsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct()
            .ToList(),
        IsDefault  = IsDefault,
    };

    public static ApplicationEntryRow FromEntry(ApplicationEntry a) => new()
    {
        Id           = a.Id,
        Name         = a.Name,
        AppPath      = a.AppPath,
        Args         = a.Args,
        ExtensionsRaw = string.Join(", ", a.Extensions),
        IsDefault    = a.IsDefault,
    };
}

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<ApplicationEntryRow> _appRows = [];

    private NumericUpDown _recentCount = null!;
    private DataGrid _appsGrid = null!;
    private CheckBox _confirmExit = null!;
    private CheckBox _skipPasteConfirm = null!;
    private CheckBox _skipNewlinePasteConfirm = null!;
    private CheckBox _sftpUse12HourTime = null!;
    private ComboBox _tmuxLegacyMode = null!;

    // The dropdown's items, in order.
    private static readonly TmuxLegacyMode[] TmuxLegacyModes =
        [TmuxLegacyMode.WhenNeeded, TmuxLegacyMode.Always, TmuxLegacyMode.Never];
    private TextBox  _builtInExtBox = null!;
    private CheckBox _builtInDefaultCheck = null!;

    /// <summary>Raised when the user clicks "Sync from SSH config…" in the General tab.</summary>
    public event EventHandler? SyncSshConfigRequested;
    /// <summary>Raised when the user clicks "Import SSH config file…" in the General tab.</summary>
    public event EventHandler? ImportSshConfigFileRequested;

    public SettingsWindow()
    {
        InitializeComponent();

        _recentCount        = this.FindControl<NumericUpDown>("RecentCountSpinner")!;
        _appsGrid           = this.FindControl<DataGrid>("AppsGrid")!;
        _confirmExit        = this.FindControl<CheckBox>("ConfirmExitCheckBox")!;
        _skipPasteConfirm   = this.FindControl<CheckBox>("SkipPasteConfirmCheckBox")!;
        _skipNewlinePasteConfirm = this.FindControl<CheckBox>("SkipNewlinePasteConfirmCheckBox")!;
        _sftpUse12HourTime  = this.FindControl<CheckBox>("SftpUse12HourTimeCheckBox")!;
        _tmuxLegacyMode     = this.FindControl<ComboBox>("TmuxLegacyModeComboBox")!;
        _builtInExtBox      = this.FindControl<TextBox>("BuiltInExtBox")!;
        _builtInDefaultCheck = this.FindControl<CheckBox>("BuiltInDefaultCheck")!;

        _recentCount.Value = SettingsService.App.RecentSessionsCount;
        _confirmExit.IsChecked = SettingsService.App.ConfirmExitWithOpenSessions;
        _skipPasteConfirm.IsChecked = SettingsService.App.SkipPasteConfirmation;
        _skipNewlinePasteConfirm.IsChecked = SettingsService.App.SkipNewlinePasteConfirmation;
        _sftpUse12HourTime.IsChecked = SettingsService.App.SftpUse12HourTime;
        _tmuxLegacyMode.SelectedIndex = Math.Max(0, Array.IndexOf(TmuxLegacyModes, SettingsService.App.TmuxLegacyMode));

        // Populate built-in editor controls
        var builtIn = SettingsService.App.Applications
            .FirstOrDefault(a => a.Kind == ApplicationKind.TermThingEditor);
        _builtInExtBox.Text = builtIn != null ? string.Join(", ", builtIn.Extensions) : string.Empty;
        _builtInDefaultCheck.IsChecked = builtIn?.IsDefault ?? false;

        // Populate external apps
        foreach (var a in SettingsService.App.Applications.Where(a => a.Kind != ApplicationKind.TermThingEditor))
            _appRows.Add(ApplicationEntryRow.FromEntry(a));

        _appsGrid.ItemsSource = _appRows;
    }

    /// <summary>Switches to the Applications tab. Called by the SFTP view's "Manage applications…" link.</summary>
    public void OpenToApplications(string? extension = null)
    {
        var tc = this.FindDescendantOfType<TabControl>();
        if (tc != null) tc.SelectedIndex = 1;

        if (!string.IsNullOrWhiteSpace(extension))
        {
            var row = new ApplicationEntryRow { ExtensionsRaw = extension };
            _appRows.Add(row);
            _appsGrid.SelectedItem = row;
            _appsGrid.ScrollIntoView(row, null);
        }
    }

    // -----------------------------------------------------------------------
    // Button handlers
    // -----------------------------------------------------------------------

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        _appsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        SettingsService.App.RecentSessionsCount = (int)(_recentCount.Value ?? 10);
        SettingsService.App.ConfirmExitWithOpenSessions = _confirmExit.IsChecked == true;
        SettingsService.App.SkipPasteConfirmation = _skipPasteConfirm.IsChecked == true;
        SettingsService.App.SkipNewlinePasteConfirmation = _skipNewlinePasteConfirm.IsChecked == true;
        SettingsService.App.SftpUse12HourTime = _sftpUse12HourTime.IsChecked == true;
        if (_tmuxLegacyMode.SelectedIndex is >= 0 and var i && i < TmuxLegacyModes.Length)
            SettingsService.App.TmuxLegacyMode = TmuxLegacyModes[i];

        var newApps = new List<ApplicationEntry>();

        // Save built-in editor entry
        var builtInExts = (_builtInExtBox.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant())
            .Distinct()
            .ToList();
        newApps.Add(new ApplicationEntry
        {
            Id         = ApplicationEntry.BuiltInEditorId,
            Name       = "TermThing Editor",
            Kind       = ApplicationKind.TermThingEditor,
            Extensions = builtInExts,
            IsDefault  = _builtInDefaultCheck.IsChecked == true,
        });

        // Save external apps (skip blank rows)
        newApps.AddRange(
            _appRows
                .Where(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.AppPath))
                .Select(r => r.ToEntry()));

        SettingsService.App.Applications = newApps;
        SettingsService.SaveApp();
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    // SSH config import — handlers raise events so MainWindow can perform the
    // work (it owns the config and tree). Settings stays a passive UI host.
    private void OnSyncSshConfigClicked(object? sender, RoutedEventArgs e)       => SyncSshConfigRequested?.Invoke(this, EventArgs.Empty);
    private void OnImportSshConfigFileClicked(object? sender, RoutedEventArgs e) => ImportSshConfigFileRequested?.Invoke(this, EventArgs.Empty);

    private void OnAddAppClicked(object? sender, RoutedEventArgs e)
    {
        var row = new ApplicationEntryRow { Name = "New App", AppPath = string.Empty };
        _appRows.Add(row);
        _appsGrid.SelectedItem = row;
        _appsGrid.ScrollIntoView(row, null);
        _appsGrid.BeginEdit();
    }

    private void OnRemoveAppClicked(object? sender, RoutedEventArgs e)
    {
        if (_appsGrid.SelectedItem is ApplicationEntryRow row)
            _appRows.Remove(row);
    }

    private async void OnBrowseAppClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not ApplicationEntryRow row)
            return;

        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select application",
            AllowMultiple = false,
        });

        var picked = files?.FirstOrDefault();
        if (picked == null) return;

        row.AppPath = picked.TryGetLocalPath() ?? picked.Path.LocalPath;
        if (string.IsNullOrWhiteSpace(row.Name) || row.Name == "New App")
            row.Name = Path.GetFileNameWithoutExtension(row.AppPath);

        // Refresh the DataGrid
        _appsGrid.ItemsSource = null;
        _appsGrid.ItemsSource = _appRows;
    }
}
