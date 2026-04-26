using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using System.Collections.ObjectModel;
using TermThing.Configuration;

namespace TermThing.Views;

/// <summary>
/// Editable proxy for <see cref="FileAssociation"/> so the DataGrid can mutate
/// the rows in place before the user commits.
/// </summary>
public sealed class FileAssociationRow
{
    public string Extension { get; set; } = string.Empty;
    public string AppPath   { get; set; } = string.Empty;
    public string Args      { get; set; } = string.Empty;

    public FileAssociation ToAssociation() => new()
    {
        Extension = Extension.ToLowerInvariant().Trim(),
        AppPath   = AppPath.Trim(),
        Args      = Args.Trim(),
    };

    public static FileAssociationRow FromAssociation(FileAssociation a) => new()
    {
        Extension = a.Extension,
        AppPath   = a.AppPath,
        Args      = a.Args,
    };
}

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<FileAssociationRow> _rows = [];

    private NumericUpDown _recentCount = null!;
    private DataGrid _assocGrid = null!;

    // True when the user pressed OK
    public bool Committed { get; private set; }

    public SettingsWindow()
    {
        InitializeComponent();

        _recentCount = this.FindControl<NumericUpDown>("RecentCountSpinner")!;
        _assocGrid   = this.FindControl<DataGrid>("AssocGrid")!;

        // Populate from current settings
        _recentCount.Value = SettingsService.App.RecentSessionsCount;

        foreach (var a in SettingsService.App.FileAssociations)
            _rows.Add(FileAssociationRow.FromAssociation(a));

        _assocGrid.ItemsSource = _rows;
    }

    /// <summary>
    /// Call from MainWindow to pre-open on the File Associations tab and optionally
    /// pre-fill a new row for a specific extension.
    /// </summary>
    public void OpenToFileAssociation(string? extension = null)
    {
        // Switch to the File Associations tab (index 1) by finding the TabControl in the tree
        var tc = this.FindDescendantOfType<TabControl>();
        if (tc != null) tc.SelectedIndex = 1;

        if (!string.IsNullOrWhiteSpace(extension))
        {
            var row = new FileAssociationRow { Extension = extension };
            _rows.Add(row);
            _assocGrid.SelectedItem = row;
            _assocGrid.ScrollIntoView(row, null);
        }
    }

    // -----------------------------------------------------------------------
    // Button handlers
    // -----------------------------------------------------------------------

    private void OnOkClicked(object? sender, RoutedEventArgs e)
    {
        // Commit edits — end any in-progress cell edit first
        _assocGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        SettingsService.App.RecentSessionsCount = (int)(_recentCount.Value ?? 10);
        SettingsService.App.FileAssociations =
            _rows.Where(r => !string.IsNullOrWhiteSpace(r.Extension) &&
                             !string.IsNullOrWhiteSpace(r.AppPath))
                 .Select(r => r.ToAssociation())
                 .ToList();

        SettingsService.SaveApp();
        Committed = true;
        Close();
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnAddAssocClicked(object? sender, RoutedEventArgs e)
    {
        var row = new FileAssociationRow { Extension = ".ext" };
        _rows.Add(row);
        _assocGrid.SelectedItem = row;
        _assocGrid.ScrollIntoView(row, null);
        _assocGrid.BeginEdit();
    }

    private void OnRemoveAssocClicked(object? sender, RoutedEventArgs e)
    {
        if (_assocGrid.SelectedItem is FileAssociationRow row)
            _rows.Remove(row);
    }

    private async void OnBrowseAppClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not FileAssociationRow row)
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
        // Refresh the DataGrid row
        _assocGrid.ItemsSource = null;
        _assocGrid.ItemsSource = _rows;
    }
}
