using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace TermThing.Panes;

/// <summary>
/// Shows a <see cref="PaneLayout"/>: one control per pane, with draggable bars on
/// the separators. The layout is in character cells; this view sizes the grid from
/// the pixel size of one cell (<see cref="CellSizeProvider"/>) and stretches it to fill,
/// so each separator cell becomes a thin bar with the panes taking the rest.
/// <para>
/// The view only arranges controls. Creating and killing the terminals behind the
/// panes belongs to the session: the view asks through <see cref="SplitRequested"/>
/// and <see cref="CloseRequested"/>, and the session answers with
/// <see cref="AddPane"/> / <see cref="RemovePane"/>.
/// </para>
/// <para>
/// Pane controls are added once and never re-parented: splits, closes and zoom only
/// add or remove other children, or hide panes. A TerminalControl that is detached
/// from the visual tree kills its process, so this matters.
/// </para>
/// </summary>
public sealed class PaneLayoutView : Panel
{
    private const double BarThickness = 4;
    private static readonly IBrush ActiveBorderBrush = new SolidColorBrush(Color.Parse("#2196F3"));
    private static readonly IBrush BarBrush = new SolidColorBrush(Color.Parse("#3a3a3a"));
    private static readonly IBrush BarHoverBrush = new SolidColorBrush(Color.Parse("#2196F3"));
    private static readonly TimeSpan DragApplyInterval = TimeSpan.FromMilliseconds(40);

    private readonly Dictionary<int, Border> _chrome = new();   // pane id → wrapper
    private readonly Dictionary<Control, int> _ids = new();      // pane control → id
    private readonly Dictionary<(LayoutNode, int), Border> _bars = new();
    private readonly List<int> _mru = new();                     // most recently active last
    private PaneLayout _layout;
    private int _nextId = 1;
    private int _activeId;
    private int? _zoomedId;

    public PaneLayoutView(Control first)
    {
        _layout = PaneLayout.Single(0, 80, 24);
        AddChrome(0, first);
        _mru.Add(0);
        ClipToBounds = true;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    public PaneLayout Layout => _layout;
    public int PaneCount => _chrome.Count;
    public IEnumerable<Control> Panes => _ids.Keys;
    public Control ActivePane => (Control)_chrome[_activeId].Child!;
    public bool IsZoomed => _zoomedId is not null;

    /// <summary>Pixel size of one character cell; defaults to 8×17 when unset or unknown.</summary>
    public Func<Size>? CellSizeProvider { get; set; }

    /// <summary>Raised (UI thread) when the active pane changes.</summary>
    public event EventHandler? ActivePaneChanged;

    /// <summary>Keyboard/menu asked for a split of the active pane. Null = splitting unsupported.</summary>
    public Action<SplitAxis>? SplitRequested { get; set; }

    /// <summary>Keyboard/menu asked to close a pane. The owner kills it and calls <see cref="RemovePane"/>.</summary>
    public Action<Control>? CloseRequested { get; set; }

    // -----------------------------------------------------------------------
    // Structure
    // -----------------------------------------------------------------------

    public bool CanSplitActive(SplitAxis axis) => _layout.CanSplit(_activeId, axis);

    /// <summary>Splits the active pane and puts <paramref name="pane"/> in the new half, active.</summary>
    public bool AddPane(Control pane, SplitAxis axis)
    {
        if (_zoomedId is not null) SetZoom(null);
        int id = _nextId++;
        if (!_layout.Split(_activeId, axis, id)) return false;
        AddChrome(id, pane);
        RebuildBars();
        Activate(id, focus: true);
        return true;
    }

    /// <summary>
    /// Removes a pane (its control is detached; the caller has killed or will kill it).
    /// The most recently used remaining pane becomes active.
    /// </summary>
    public bool RemovePane(Control pane)
    {
        if (!_ids.TryGetValue(pane, out var id) || !_layout.Remove(id)) return false;
        if (_zoomedId == id) _zoomedId = null;
        var chrome = _chrome[id];
        _chrome.Remove(id);
        _ids.Remove(pane);
        _mru.Remove(id);
        Children.Remove(chrome);
        chrome.Child = null;
        RebuildBars();
        if (_activeId == id) Activate(_mru[^1], focus: true);
        else UpdateChrome();
        InvalidateMeasure();
        return true;
    }

    public void RequestSplit(SplitAxis axis)
    {
        if (CanSplitActive(axis)) SplitRequested?.Invoke(axis);
    }

    public void RequestCloseActive()
    {
        if (PaneCount > 1) CloseRequested?.Invoke(ActivePane);
    }

    private void AddChrome(int id, Control pane)
    {
        var chrome = new Border { Child = pane, BorderThickness = new Thickness(0) };
        // Track the active pane from focus, so mouse clicks and Tab moves count too.
        chrome.AddHandler(GotFocusEvent, (_, _) => Activate(id, focus: false), RoutingStrategies.Bubble, handledEventsToo: true);
        _chrome[id] = chrome;
        _ids[pane] = id;
        Children.Add(chrome);
    }

    // -----------------------------------------------------------------------
    // Active pane, focus, zoom, resize
    // -----------------------------------------------------------------------

    private void Activate(int id, bool focus)
    {
        if (!_chrome.ContainsKey(id)) return;
        bool changed = _activeId != id;
        _activeId = id;
        _mru.Remove(id);
        _mru.Add(id);
        if (_zoomedId is not null && _zoomedId != id) SetZoom(id);
        UpdateChrome();
        if (focus) Dispatcher.UIThread.Post(() => ActivePane.Focus(), DispatcherPriority.Input);
        if (changed) ActivePaneChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool MoveFocus(PaneDirection direction)
    {
        if (_zoomedId is not null) return false;
        var next = _layout.Neighbor(_activeId, direction, id => _mru.IndexOf(id));
        if (next is null) return false;
        Activate(next.Value, focus: true);
        return true;
    }

    public bool ResizeActive(PaneDirection direction, int cells)
    {
        if (_zoomedId is not null || !_layout.ResizePane(_activeId, direction, cells)) return false;
        InvalidateMeasure();
        return true;
    }

    /// <summary>Shows only the active pane, full size (tmux <c>resize-pane -Z</c>), or back.</summary>
    public void ToggleZoom() => SetZoom(_zoomedId is null && PaneCount > 1 ? _activeId : null);

    private void SetZoom(int? id)
    {
        _zoomedId = id;
        foreach (var (pid, chrome) in _chrome)
            chrome.IsVisible = id is null || pid == id;
        foreach (var bar in _bars.Values)
            bar.IsVisible = id is null;
        UpdateChrome();
        InvalidateMeasure();
    }

    /// <summary>A thin accent border marks the active pane once there is more than one.</summary>
    private void UpdateChrome()
    {
        bool framed = PaneCount > 1 && _zoomedId is null;
        foreach (var (id, chrome) in _chrome)
        {
            chrome.BorderThickness = new Thickness(framed ? 1 : 0);
            chrome.BorderBrush = id == _activeId ? ActiveBorderBrush : Brushes.Transparent;
        }
    }

    // -----------------------------------------------------------------------
    // Keyboard
    // -----------------------------------------------------------------------

    /// <summary>
    /// Pane shortcuts, following Windows Terminal: Alt+Shift+Plus split right,
    /// Alt+Shift+Minus split down; with several panes also Alt+Arrow to move focus,
    /// Alt+Shift+Arrow to resize, Alt+Shift+Z to zoom and Ctrl+Shift+W to close.
    /// Handled in the tunnel phase so the terminal never sees them; navigation keys
    /// only while there is something to navigate, so a single pane keeps Alt+Arrow.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        bool multi = PaneCount > 1;

        if (mods == (KeyModifiers.Alt | KeyModifiers.Shift))
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:      RequestSplit(SplitAxis.LeftRight); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract: RequestSplit(SplitAxis.TopBottom); e.Handled = true; return;
                case Key.Z when multi:            ToggleZoom(); e.Handled = true; return;
            }
            if (multi && ArrowDirection(e.Key) is { } dir)
            {
                ResizeActive(dir, dir is PaneDirection.Left or PaneDirection.Right ? 2 : 1);
                e.Handled = true;
            }
            return;
        }

        if (multi && mods == KeyModifiers.Alt && ArrowDirection(e.Key) is { } move)
        {
            MoveFocus(move);
            e.Handled = true;
            return;
        }

        if (multi && mods == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key == Key.W)
        {
            RequestCloseActive();
            e.Handled = true;
        }
    }

    private static PaneDirection? ArrowDirection(Key key) => key switch
    {
        Key.Left  => PaneDirection.Left,
        Key.Right => PaneDirection.Right,
        Key.Up    => PaneDirection.Up,
        Key.Down  => PaneDirection.Down,
        _         => null,
    };

    // -----------------------------------------------------------------------
    // Divider bars
    // -----------------------------------------------------------------------

    private void RebuildBars()
    {
        foreach (var bar in _bars.Values) Children.Remove(bar);
        _bars.Clear();
        AddBars(_layout.Root);
        UpdateChrome();
    }

    private void AddBars(LayoutNode node)
    {
        if (node.IsLeaf) return;
        for (int i = 0; i + 1 < node.Children.Count; i++)
        {
            var bar = CreateBar(node, i);
            _bars[(node, i)] = bar;
            Children.Add(bar);
        }
        foreach (var c in node.Children) AddBars(c);
    }

    private Border CreateBar(LayoutNode split, int index)
    {
        bool vertical = split.Axis == SplitAxis.LeftRight; // bar between side-by-side panes
        var line = new Border
        {
            Background = BarBrush,
            Width = vertical ? 1 : double.NaN,
            Height = vertical ? double.NaN : 1,
            HorizontalAlignment = vertical ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? Avalonia.Layout.VerticalAlignment.Stretch : Avalonia.Layout.VerticalAlignment.Center,
        };
        var bar = new Border
        {
            Background = Brushes.Transparent, // hit-testable across the full bar thickness
            Child = line,
            Cursor = new Cursor(vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth),
            IsVisible = _zoomedId is null,
        };

        Point start = default;
        int startSize = 0;
        int appliedSize = 0;
        bool dragging = false;
        var lastApply = DateTime.MinValue;

        void Apply(Point p, bool final)
        {
            var cell = CellSize();
            double cellPx = vertical ? cell.Width : cell.Height;
            double delta = vertical ? p.X - start.X : p.Y - start.Y;
            int target = startSize + (int)Math.Round(delta / cellPx);
            int current = split.Children[index].SizeAlong(split.Axis);
            if (target == current) return;
            // Throttle live updates: every applied step resizes two or more PTYs.
            if (!final && DateTime.UtcNow - lastApply < DragApplyInterval) return;
            lastApply = DateTime.UtcNow;
            _layout.MoveDivider(split, index, target - current);
            appliedSize = split.Children[index].SizeAlong(split.Axis);
            InvalidateMeasure();
        }

        bar.PointerEntered += (_, _) => line.Background = BarHoverBrush;
        bar.PointerExited += (_, _) => { if (!dragging) line.Background = BarBrush; };
        bar.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                _layout.Equalize(split);
                InvalidateMeasure();
                e.Handled = true;
                return;
            }
            dragging = true;
            start = e.GetPosition(this);
            startSize = appliedSize = split.Children[index].SizeAlong(split.Axis);
            e.Pointer.Capture(bar);
            e.Handled = true;
        };
        bar.PointerMoved += (_, e) => { if (dragging) Apply(e.GetPosition(this), final: false); };
        bar.PointerReleased += (_, e) =>
        {
            if (!dragging) return;
            Apply(e.GetPosition(this), final: true);
            dragging = false;
            e.Pointer.Capture(null);
            line.Background = BarBrush;
            Dispatcher.UIThread.Post(() => ActivePane.Focus(), DispatcherPriority.Input);
        };
        bar.PointerCaptureLost += (_, _) => { dragging = false; line.Background = BarBrush; };
        return bar;
    }

    // -----------------------------------------------------------------------
    // Layout
    // -----------------------------------------------------------------------

    private Size CellSize()
    {
        var s = CellSizeProvider?.Invoke() ?? default;
        return s.Width > 0 && s.Height > 0 ? s : new Size(8, 17);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height);
        var cell = CellSize();
        _layout.Resize((int)Math.Floor(size.Width / cell.Width), (int)Math.Floor(size.Height / cell.Height));
        foreach (var (child, rect) in Rects(size))
            child.Measure(rect.Size);
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cell = CellSize();
        _layout.Resize((int)Math.Floor(finalSize.Width / cell.Width), (int)Math.Floor(finalSize.Height / cell.Height));
        foreach (var (child, rect) in Rects(finalSize))
            child.Arrange(rect);
        return finalSize;
    }

    /// <summary>Pixel rectangles for every visible pane and bar.</summary>
    private List<(Control, Rect)> Rects(Size size)
    {
        var result = new List<(Control, Rect)>();
        if (_zoomedId is { } z)
        {
            result.Add((_chrome[z], new Rect(size)));
            return result;
        }
        // Cell → pixel scale that stretches the grid over the whole view.
        double sx = size.Width / Math.Max(1, _layout.Width);
        double sy = size.Height / Math.Max(1, _layout.Height);
        Place(_layout.Root, new Rect(size), sx, sy, result);
        return result;
    }

    /// <summary>
    /// Lays out <paramref name="node"/> in <paramref name="rect"/>. Each separator cell
    /// becomes a bar centred on it; the panes on either side grow into the rest of the
    /// cell, so panes are a little more than their cell size rather than leaving gaps.
    /// </summary>
    private void Place(LayoutNode node, Rect rect, double sx, double sy, List<(Control, Rect)> result)
    {
        if (node.IsLeaf)
        {
            if (_chrome.TryGetValue(node.PaneId, out var chrome)) result.Add((chrome, rect));
            return;
        }

        bool lr = node.Axis == SplitAxis.LeftRight;
        double half = BarThickness / 2;
        double from = lr ? rect.Left : rect.Top;
        for (int i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            bool last = i == node.Children.Count - 1;
            // Centre of the separator cell after this child, in pixels.
            double centre = lr ? (child.X + child.Width + 0.5) * sx : (child.Y + child.Height + 0.5) * sy;
            double to = last ? (lr ? rect.Right : rect.Bottom) : centre - half;

            var childRect = lr
                ? new Rect(from, rect.Top, Math.Max(0, to - from), rect.Height)
                : new Rect(rect.Left, from, rect.Width, Math.Max(0, to - from));
            Place(child, childRect, sx, sy, result);

            if (!last && _bars.TryGetValue((node, i), out var bar))
            {
                result.Add((bar, lr
                    ? new Rect(centre - half, rect.Top, BarThickness, rect.Height)
                    : new Rect(rect.Left, centre - half, rect.Width, BarThickness)));
            }
            from = centre + half;
        }
    }
}
