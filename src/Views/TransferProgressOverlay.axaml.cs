using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TermThing.Sftp;

namespace TermThing.Views;

/// <summary>
/// A thin overlay pinned to the bottom of the SFTP panel showing the currently
/// active file-transfer progress with a Cancel button.
/// </summary>
public partial class TransferProgressOverlay : UserControl
{
    private TextBlock _label = null!;
    private ProgressBar _bar = null!;

    private TransferQueue? _queue;

    public TransferProgressOverlay()
    {
        InitializeComponent();
        _label = this.FindControl<TextBlock>("TransferLabel")!;
        _bar   = this.FindControl<ProgressBar>("TransferProgress")!;
    }

    public void Bind(TransferQueue queue)
    {
        _queue = queue;
        queue.ProgressChanged += OnProgress;
        queue.QueueEmpty      += OnQueueEmpty;
    }

    private void OnProgress(object? sender, TransferProgressEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsVisible = true;
            _label.Text = e.FileName;

            if (e.TotalBytes > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = e.TotalBytes == 0 ? 0 :
                    (double)e.TransferredBytes / e.TotalBytes * 100;
            }
            else
            {
                _bar.IsIndeterminate = true;
            }
        });
    }

    private void OnQueueEmpty(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => IsVisible = false);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        _queue?.Cancel();
    }
}
