using Avalonia.Controls;
using TermThing.Ssh;

namespace TermThing.Views;

public partial class SysmonPanel : UserControl
{
    private TextBlock _statsText = null!;

    public SysmonPanel()
    {
        InitializeComponent();
        _statsText = this.FindControl<TextBlock>("StatsText")!;
    }

    /// <summary>
    /// Updates the stats line. Called on the UI thread by <see cref="SysmonPoller"/>'s
    /// <c>SnapshotReceived</c> event handler.
    /// </summary>
    public void Update(SysmonSnapshot s)
    {
        if (s.Failed)
        {
            _statsText.Text = s.Error ?? "(stats unavailable)";
            return;
        }

        var cpu = double.IsNaN(s.CpuPercent) ? "--" : $"{s.CpuPercent,4:F1}%";
        var mem = s.MemTotalGb > 0
            ? $"{s.MemUsedGb:F1}/{s.MemTotalGb:F1} GB ({s.MemPercent:F0}%)"
            : "--";
        _statsText.Text = $"CPU {cpu}  •  Mem {mem}  •  Load {s.LoadAvg}  •  Up {s.Uptime}  •  / {s.DiskRootPercent:F0}%";
    }
}
