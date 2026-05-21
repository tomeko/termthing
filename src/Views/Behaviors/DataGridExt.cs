using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace TermThing.Views.Behaviors;

/// <summary>
/// Attached behaviours for <see cref="DataGrid"/>:
///
/// <list type="bullet">
///   <item><b>DeselectOnEmptyClick</b> — clears the selection when the user
///         clicks below the last row, on column headers, or otherwise misses
///         every <see cref="DataGridRow"/>.</item>
///   <item><b>ToggleOffSoleSelection</b> — when the user bare-left-clicks the
///         row that is already the only selected one, clears the selection on
///         pointer release. Standard multi-select modifiers (Shift/Ctrl) are
///         left alone.</item>
/// </list>
/// </summary>
public static class DataGridExt
{
    // -----------------------------------------------------------------------
    // DeselectOnEmptyClick
    // -----------------------------------------------------------------------

    public static readonly AttachedProperty<bool> DeselectOnEmptyClickProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>(
            "DeselectOnEmptyClick",
            typeof(DataGridExt));

    public static void SetDeselectOnEmptyClick(DataGrid grid, bool value) =>
        grid.SetValue(DeselectOnEmptyClickProperty, value);

    public static bool GetDeselectOnEmptyClick(DataGrid grid) =>
        grid.GetValue(DeselectOnEmptyClickProperty);

    private static void OnDeselectOnEmptyClickChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
            grid.AddHandler(InputElement.PointerPressedEvent, OnGridPointerPressed, handledEventsToo: false);
        else
            grid.RemoveHandler(InputElement.PointerPressedEvent, OnGridPointerPressed);
    }

    private static void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;

        // If the pointer hit a DataGridRow (or a child of one), let normal selection proceed.
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>() is not null) return;

        // Clicked outside any row — clear selection.
        grid.SelectedItem = null;
    }

    // -----------------------------------------------------------------------
    // ToggleOffSoleSelection
    // -----------------------------------------------------------------------

    public static readonly AttachedProperty<bool> ToggleOffSoleSelectionProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>(
            "ToggleOffSoleSelection",
            typeof(DataGridExt));

    public static void SetToggleOffSoleSelection(DataGrid grid, bool value) =>
        grid.SetValue(ToggleOffSoleSelectionProperty, value);

    public static bool GetToggleOffSoleSelection(DataGrid grid) =>
        grid.GetValue(ToggleOffSoleSelectionProperty);

    // Per-grid scratch flag, captured on pointer-pressed and consumed on
    // pointer-released. ConditionalWeakTable so the flag is GC'd with the grid.
    private sealed class ToggleState { public bool ClickedSole; }
    private static readonly ConditionalWeakTable<DataGrid, ToggleState> _toggleState = new();

    private static void OnToggleOffSoleSelectionChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            _toggleState.GetValue(grid, _ => new ToggleState());
            grid.AddHandler(InputElement.PointerPressedEvent,  OnTogglePressed,  RoutingStrategies.Tunnel);
            grid.AddHandler(InputElement.PointerReleasedEvent, OnToggleReleased, RoutingStrategies.Tunnel);
        }
        else
        {
            grid.RemoveHandler(InputElement.PointerPressedEvent,  OnTogglePressed);
            grid.RemoveHandler(InputElement.PointerReleasedEvent, OnToggleReleased);
            _toggleState.Remove(grid);
        }
    }

    private static void OnTogglePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!_toggleState.TryGetValue(grid, out var state)) return;

        state.ClickedSole = false;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers != KeyModifiers.None) return;  // Shift/Ctrl = leave to DataGrid

        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>();
        if (row?.DataContext is null) return;

        var sel = grid.SelectedItems;
        if (sel != null && sel.Count == 1 && ReferenceEquals(sel[0], row.DataContext))
            state.ClickedSole = true;
    }

    private static void OnToggleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!_toggleState.TryGetValue(grid, out var state)) return;

        if (state.ClickedSole)
        {
            // Avalonia's DataGrid throws InvalidOperationException if you try to
            // mutate SelectedItems while SelectionMode == Single. Use the right
            // API for the mode.
            if (grid.SelectionMode == DataGridSelectionMode.Single)
                grid.SelectedItem = null;
            else
                grid.SelectedItems?.Clear();
        }
        state.ClickedSole = false;
    }

    // -----------------------------------------------------------------------

    static DataGridExt()
    {
        DeselectOnEmptyClickProperty.Changed.AddClassHandler<DataGrid>(OnDeselectOnEmptyClickChanged);
        ToggleOffSoleSelectionProperty.Changed.AddClassHandler<DataGrid>(OnToggleOffSoleSelectionChanged);
    }
}
