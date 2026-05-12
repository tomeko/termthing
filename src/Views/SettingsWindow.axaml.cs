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
    private TextBox  _builtInExtBox = null!;
    private CheckBox _builtInDefaultCheck = null!;

    // True when the user pressed OK
    public bool Committed { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();

        _recentCount        = this.FindControl<NumericUpDown>("RecentCountSpinner")!;
        _appsGrid           = this.FindControl<DataGrid>("AppsGrid")!;
        _confirmExit        = this.FindControl<CheckBox>("ConfirmExitCheckBox")!;
        _builtInExtBox      = this.FindControl<TextBox>("BuiltInExtBox")!;
        _builtInDefaultCheck = this.FindControl<CheckBox>("BuiltInDefaultCheck")!;

        _recentCount.Value = SettingsService.App.RecentSessionsCount;
        _confirmExit.IsChecked = SettingsService.App.ConfirmExitWithOpenSessions;

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
        Committed = true;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

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
