using Avalonia.Controls;
using Avalonia.Interactivity;
using TermThing.Configuration;
using TermThing.Sessions.Launchers;

namespace TermThing.Views;

public partial class LocalShellPickerDialog : Window
{
    private readonly IReadOnlyList<LocalShells.ShellOption> _options;

    /// <summary>The shell chosen by the user; null if the dialog was cancelled.</summary>
    public LocalShells.ShellOption? SelectedShell { get; private set; }

    public LocalShellPickerDialog()
    {
        InitializeComponent();

        _options = LocalShells.Available;
        ShellComboBox.ItemsSource = _options.Select(o => o.Label).ToList();

        // Pre-select the last-used shell (falls back to index 0 when not found).
        var last = SettingsService.Temp.LastLocalShell;
        var idx  = _options.ToList().FindIndex(o => o.Process == last);
        ShellComboBox.SelectedIndex = idx >= 0 ? idx : 0;

        Opened += (_, _) => ShellComboBox.Focus();
    }

    private void OnStartClicked(object? sender, RoutedEventArgs e)
    {
        var idx = ShellComboBox.SelectedIndex;
        if (idx < 0 || idx >= _options.Count) return;

        SelectedShell = _options[idx];

        // Persist the choice so the same shell is pre-selected next time.
        SettingsService.Temp.LastLocalShell = SelectedShell.Process;
        SettingsService.SaveTemp();

        Close(true);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);
}
