using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.ObjectModel;
using TermThing.Ssh;

namespace TermThing.Views;

public partial class DockerMonPanel : UserControl
{
    private readonly DockerMonPoller _poller;
    private CheckBox _showAllCheck = null!;
    private TextBlock _statusText = null!;
    private Button _refreshButton = null!;
    private DataGrid _containersGrid = null!;

    public ObservableCollection<DockerContainerRow> Containers { get; } = new();

    public DockerMonPanel(DockerMonPoller poller)
    {
        _poller = poller;
        InitializeComponent();

        _showAllCheck   = this.FindControl<CheckBox>("ShowAllCheck")!;
        _statusText     = this.FindControl<TextBlock>("StatusText")!;
        _refreshButton  = this.FindControl<Button>("RefreshButton")!;
        _containersGrid = this.FindControl<DataGrid>("ContainersGrid")!;

        _containersGrid.ItemsSource = Containers;

        _poller.Snapshot += OnSnapshot;
        _poller.Error    += OnPollerError;

        _showAllCheck.IsCheckedChanged += (_, _) =>
        {
            _poller.ShowAll = _showAllCheck.IsChecked == true;
            _poller.RefreshNow();
        };
        _refreshButton.Click += (_, _) => _poller.RefreshNow();
    }

    private void OnSnapshot(object? sender, IReadOnlyList<DockerContainerRow> snapshot)
    {
        // Merge by ID — update existing rows, add new, remove gone. Preserves selection.
        var byId = snapshot.ToDictionary(r => r.Id);

        // Remove gone
        for (int i = Containers.Count - 1; i >= 0; i--)
        {
            if (!byId.ContainsKey(Containers[i].Id))
                Containers.RemoveAt(i);
        }

        // Add new + update existing
        foreach (var fresh in snapshot)
        {
            var existing = Containers.FirstOrDefault(c => c.Id == fresh.Id);
            if (existing == null)
            {
                Containers.Add(fresh);
            }
            else
            {
                existing.Name   = fresh.Name;
                existing.Image  = fresh.Image;
                existing.Status = fresh.Status;
                existing.Ports  = fresh.Ports;
            }
        }

        if (_statusText.Text?.StartsWith("docker", StringComparison.OrdinalIgnoreCase) == true)
            _statusText.Text = string.Empty;
    }

    private void OnPollerError(object? sender, string msg)
    {
        _statusText.Text = msg;
    }

    private async void OnStopClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DockerContainerRow row) return;
        row.Busy = true;
        try { await _poller.StopAsync(row.Id); }
        finally { row.Busy = false; _poller.RefreshNow(); }
    }

    private async void OnRestartClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DockerContainerRow row) return;
        row.Busy = true;
        try { await _poller.RestartAsync(row.Id); }
        finally { row.Busy = false; _poller.RefreshNow(); }
    }

    private void OnLogsClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DockerContainerRow row) return;
        _poller.RequestLogs(row);
    }
}
