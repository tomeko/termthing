using Avalonia.Threading;
using Iciclecreek.Terminal;
using Porta.Pty;
using System.Reflection;

namespace TermThing.Ssh;

/// <summary>
/// Extension method that back-ports AttachConnection onto the NuGet-published
/// TerminalControl, which removed that API after the version currently on NuGet.
/// Implemented via reflection over private TerminalView fields.
/// </summary>
internal static class TerminalControlExtensions
{
    // TerminalView private field / method names (stable across 1.0.x builds)
    private static readonly FieldInfo? s_ptyConnField =
        typeof(TerminalView).GetField("_ptyConnection",  BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? s_ctsField =
        typeof(TerminalView).GetField("_processCts",     BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? s_exitHandledField =
        typeof(TerminalView).GetField("_processExitHandled", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? s_cleanupMethod =
        typeof(TerminalView).GetMethod("CleanupProcess",  BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? s_readMethod =
        typeof(TerminalView).GetMethod("ReadPtyOutputAsync", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo? s_onExitedMethod =
        typeof(TerminalView).GetMethod("OnPtyProcessExited", BindingFlags.NonPublic | BindingFlags.Instance);

    /// <summary>
    /// Attaches a pre-built <see cref="IPtyConnection"/> (e.g. an SSH shell stream)
    /// to the terminal control, bypassing the local PTY process-launch path.
    /// </summary>
    public static async Task AttachConnection(this TerminalControl tc, IPtyConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // Ensure the TemplatedControl template has been applied so _terminalView is set.
        if (tc.GetValue(TerminalControl.ProcessProperty) is not null)
        {
            // ApplyTemplate is protected; invoke it via TemplatedControl.ApplyTemplate().
            tc.ApplyTemplate();
        }

        // Retrieve _terminalView via reflection on TerminalControl.
        var tvField = typeof(TerminalControl).GetField("_terminalView",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var terminalView = tvField?.GetValue(tc) as TerminalView
            ?? throw new InvalidOperationException(
                "TerminalControl template has not been applied yet; " +
                "call AttachConnection after the control is loaded.");

        AttachToView(terminalView, connection);

        // Focus the TerminalView using the same deferred approach as TerminalControl.LaunchProcess,
        // so that the Avalonia input system has settled before we claim focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (!terminalView.IsFocused)
                terminalView.Focus();
        }, DispatcherPriority.Input);

        await Task.CompletedTask; // keep async signature consistent with original API
    }

    private static void AttachToView(TerminalView view, IPtyConnection connection)
    {
        // 1. Cancel & dispose any existing process.
        s_cleanupMethod?.Invoke(view, null);

        // 2. Create a fresh cancellation token source.
        var cts = new CancellationTokenSource();
        s_ctsField?.SetValue(view, cts);

        // 3. Reset the process-exit guard (int field, 0 = not handled).
        s_exitHandledField?.SetValue(view, 0);

        // 4. Store the connection.
        s_ptyConnField?.SetValue(view, connection);

        // 5. Subscribe to the ProcessExited event so the terminal shows the exit code.
        if (s_onExitedMethod != null)
        {
            EventHandler<PtyExitedEventArgs> handler = (sender, e) =>
                s_onExitedMethod.Invoke(view, new object?[] { sender, e });
            connection.ProcessExited += handler;
        }

        // 6. Resize the SSH PTY to match the current terminal dimensions.
        try { connection.Resize(view.Terminal.Cols, view.Terminal.Rows); }
        catch { /* Some connections don't support resize */ }

        // 7. Start the read loop (mirrors TerminalView.LaunchProcess internals).
        if (s_readMethod != null)
        {
            _ = Task.Run(() =>
            {
                var task = (Task?)s_readMethod.Invoke(view, new object[] { cts.Token });
                return task ?? Task.CompletedTask;
            });
        }
    }
}
