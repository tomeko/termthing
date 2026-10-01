using Iciclecreek.Terminal;

namespace TermThing.Panes;

/// <summary>
/// Remembers each pane's OSC title and reports the active pane's as the session's
/// title (the fallback until that pane has set one). Sessions call <see cref="Refresh"/>
/// when the active pane changes.
/// </summary>
internal sealed class PaneTitles
{
    private readonly Dictionary<TerminalControl, string> _titles = new();
    private readonly Func<TerminalControl?> _active;
    private readonly string _fallback;

    public PaneTitles(string fallback, Func<TerminalControl?> active)
    {
        _fallback = fallback;
        _active = active;
        Current = fallback;
    }

    public string Current { get; private set; }

    /// <summary>Raised (UI thread) when <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    public void Watch(TerminalControl tc) =>
        TerminalView.AddTitleChangedHandler(tc, (_, e) =>
        {
            _titles[tc] = e.Title;
            e.Handled = true;
            Refresh();
        });

    public void Forget(TerminalControl tc)
    {
        if (_titles.Remove(tc)) Refresh();
    }

    public void Refresh()
    {
        var title = _active() is { } tc && _titles.TryGetValue(tc, out var t) && !string.IsNullOrEmpty(t) ? t : _fallback;
        if (title == Current) return;
        Current = title;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
