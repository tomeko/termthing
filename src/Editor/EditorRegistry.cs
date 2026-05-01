using Avalonia.Controls;
using Avalonia.Threading;
using TermThing.Views;

namespace TermThing.Editor;

/// <summary>
/// Application-scoped registry of open <see cref="TextEditorWindow"/> instances.
/// Prevents duplicate windows for the same remote file within an SFTP session,
/// and provides coordinated teardown when a session tab is closed.
/// </summary>
public sealed class EditorRegistry
{
    private readonly Dictionary<(Guid sessionId, string remotePath), TextEditorWindow> _windows = new();

    // -----------------------------------------------------------------------
    // Open / focus
    // -----------------------------------------------------------------------

    /// <summary>
    /// Opens a new editor window for <paramref name="ctx"/>, or focuses the
    /// existing window if the file is already open.  When an existing window has
    /// unsaved changes the user is prompted to reload, keep local edits, or cancel.
    /// </summary>
    public async Task OpenOrFocusAsync(TextEditorContext ctx, string initialText)
    {
        var key = (ctx.SessionId, ctx.RemotePath);

        if (_windows.TryGetValue(key, out var existing))
        {
            if (existing.IsDirty)
            {
                var choice = await existing.PromptReloadAsync();
                if (choice == ReloadChoice.Reload)
                    existing.SetContent(initialText);
                // Keep or Cancel: just focus the existing window
            }
            BringToFront(existing);
            return;
        }

        var window = new TextEditorWindow(ctx, initialText, SyntaxCatalog.GetRegistryOptions());
        _windows[key] = window;
        window.Closed += (_, _) => _windows.Remove(key);
        window.Show();
    }

    // -----------------------------------------------------------------------
    // Session teardown
    // -----------------------------------------------------------------------

    /// <summary>
    /// For each editor window belonging to <paramref name="sessionId"/>: prompts to
    /// save dirty documents, then closes the window.  Awaiting this method before
    /// disposing the SFTP client gives dirty editors a chance to upload.
    /// </summary>
    public async Task CloseSessionEditorsAsync(Guid sessionId)
    {
        var matching = _windows
            .Where(kvp => kvp.Key.sessionId == sessionId)
            .Select(kvp => kvp.Value)
            .ToList(); // snapshot — dictionary mutates as windows close

        foreach (var win in matching)
        {
            var shouldClose = await win.RequestCloseAsync();
            if (shouldClose)
                win.ForceClose();
        }
    }

    /// <summary>
    /// Marks all editor windows for <paramref name="sessionId"/> as having a dead
    /// session (Save is disabled, a notice is shown).  Called when the SSH
    /// connection drops before the tab is explicitly closed.
    /// </summary>
    public void NotifySessionEnded(Guid sessionId)
    {
        foreach (var kvp in _windows.Where(k => k.Key.sessionId == sessionId))
            Dispatcher.UIThread.Post(() => kvp.Value.NotifySessionEnded());
    }

    /// <summary>
    /// Returns the display titles of every open editor window that has unsaved
    /// changes. Used by the main-window exit-confirmation dialog.
    /// </summary>
    public IReadOnlyList<string> GetDirtyEditorTitles()
    {
        return _windows.Values
            .Where(w => w.IsDirty)
            .Select(w => w.Title ?? string.Empty)
            .ToList();
    }

    // -----------------------------------------------------------------------

    private static void BringToFront(Window win)
    {
        if (win.WindowState == WindowState.Minimized)
            win.WindowState = WindowState.Normal;
        win.Activate();
    }
}

/// <summary>Result of the "reload / keep / cancel" prompt.</summary>
public enum ReloadChoice { Reload, Keep, Cancel }
