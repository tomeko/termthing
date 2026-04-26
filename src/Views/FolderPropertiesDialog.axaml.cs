using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Renci.SshNet;
using System;
using System.Threading.Tasks;

namespace TermThing.Views;

public partial class FolderPropertiesDialog : Window
{
    private readonly SshClient _ssh;
    private readonly string _folderPath;

    private ProgressBar _sizeProgress = null!;
    private ProgressBar _fileCountProgress = null!;
    private TextBlock _sizeText = null!;
    private TextBlock _fileCountText = null!;
    private TextBlock _errorText = null!;

    public FolderPropertiesDialog(SshClient ssh, string folderPath)
    {
        _ssh = ssh;
        _folderPath = folderPath;

        InitializeComponent();

        Title = $"Properties – {System.IO.Path.GetFileName(folderPath.TrimEnd('/'))}";
        FolderPathText.Text = folderPath;

        // Wire up named controls
        _sizeProgress      = this.FindControl<ProgressBar>("SizeProgress")!;
        _fileCountProgress = this.FindControl<ProgressBar>("FileCountProgress")!;
        _sizeText          = this.FindControl<TextBlock>("SizeText")!;
        _fileCountText     = this.FindControl<TextBlock>("FileCountText")!;
        _errorText         = this.FindControl<TextBlock>("ErrorText")!;

        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var quoted = ShellQuote(_folderPath);

            // Run du and find in parallel — SSH.NET multiplexes channels on one connection
            var sizeTask  = Task.Run(() => { using var c = _ssh.RunCommand($"du -sb -- {quoted} 2>/dev/null"); return c.Result.Trim(); });
            var countTask = Task.Run(() => { using var c = _ssh.RunCommand($"find {quoted} -not -type d 2>/dev/null | wc -l"); return c.Result.Trim(); });

            await Task.WhenAll(sizeTask, countTask);

            var sizeOutput  = sizeTask.Result;
            var countOutput = countTask.Result;

            // du output: "<bytes>\t<path>"
            string sizeDisplay;
            var firstToken = sizeOutput.Split(new[] { '\t', ' ' }, 2)[0];
            if (long.TryParse(firstToken, out var bytes))
                sizeDisplay = FormatSize(bytes);
            else
                sizeDisplay = string.IsNullOrWhiteSpace(sizeOutput) ? "unavailable" : sizeOutput;

            string countDisplay = long.TryParse(countOutput, out var count)
                ? $"{count:N0}"
                : (string.IsNullOrWhiteSpace(countOutput) ? "unavailable" : countOutput);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _sizeProgress.IsVisible      = false;
                _fileCountProgress.IsVisible = false;
                _sizeText.Text               = sizeDisplay;
                _fileCountText.Text          = countDisplay;
                _sizeText.IsVisible          = true;
                _fileCountText.IsVisible     = true;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _sizeProgress.IsVisible      = false;
                _fileCountProgress.IsVisible = false;
                _sizeText.Text               = "—";
                _fileCountText.Text          = "—";
                _sizeText.IsVisible          = true;
                _fileCountText.IsVisible     = true;
                _errorText.Text              = ex.Message;
                _errorText.IsVisible         = true;
            });
        }
    }

    private static string ShellQuote(string path)
        => "'" + path.Replace("'", "'\\''") + "'";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB  ({bytes:N0} bytes)",
        >= 1_048_576     => $"{bytes / 1_048_576.0:F2} MB  ({bytes:N0} bytes)",
        >= 1_024         => $"{bytes / 1_024.0:F1} KB  ({bytes:N0} bytes)",
        _                => $"{bytes:N0} bytes",
    };

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
