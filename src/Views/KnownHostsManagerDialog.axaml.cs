using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using TermThing.Ssh;
using TermThing.Views.Behaviors;

namespace TermThing.Views;

public partial class KnownHostsManagerDialog : Window
{
    private readonly IKnownHostsService _service;
    public ObservableCollection<KnownHostEntry> Entries { get; } = [];

    public KnownHostsManagerDialog(IKnownHostsService service)
    {
        _service = service;
        InitializeComponent();
        Refresh();
        EntriesGrid.ItemsSource = Entries;
        DataGridExt.SetDeselectOnEmptyClick(EntriesGrid, true);
        DataGridExt.SetToggleOffSoleSelection(EntriesGrid, true);
    }

    private void Refresh()
    {
        Entries.Clear();
        foreach (var e in _service.List())
            Entries.Add(e);
    }

    private void OnRemoveClicked(object? sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is KnownHostEntry entry)
        {
            _service.Remove(entry);
            Entries.Remove(entry);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
