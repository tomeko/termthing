using Avalonia.Controls;
using Avalonia.Interactivity;
using Renci.SshNet.Common;
using TermThing.Ssh;

namespace TermThing.Views;

public enum HostKeyAction { ConnectOnce, TrustAndConnect, Cancel }

public partial class HostKeyPromptDialog : Window
{
    public HostKeyAction Result { get; private set; } = HostKeyAction.Cancel;

    public HostKeyPromptDialog(string host, int port, KnownHostStatus status, HostKeyEventArgs e)
    {
        InitializeComponent();

        var fp = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(e.HostKey));

        HostText.Text = port == 22 ? host : $"{host}:{port}";
        AlgorithmText.Text = e.HostKeyName;
        FingerprintText.Text = $"SHA256:{fp}";

        var label = port == 22 ? host : $"{host}:{port}";
        if (status == KnownHostStatus.Mismatched)
        {
            Title = $"⚠ Host Key Mismatch — {label}";
            WarningText.IsVisible = true;
            WarningText.Text =
                "WARNING: The host key for this server has CHANGED since you last connected.\n" +
                "This could indicate a man-in-the-middle attack. Verify out-of-band before continuing.";
        }
        else
        {
            Title = $"Unknown Host Key — {label}";
            WarningText.IsVisible = false;
        }
    }

    private void OnConnectOnceClicked(object? sender, RoutedEventArgs e)
    {
        Result = HostKeyAction.ConnectOnce;
        Close(HostKeyAction.ConnectOnce);
    }

    private void OnTrustClicked(object? sender, RoutedEventArgs e)
    {
        Result = HostKeyAction.TrustAndConnect;
        Close(HostKeyAction.TrustAndConnect);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Result = HostKeyAction.Cancel;
        Close(HostKeyAction.Cancel);
    }
}
