using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace TermThing.Views.Behaviors;

/// <summary>
/// Attached property that clears a <see cref="DataGrid"/>'s selection when the
/// user clicks on an empty area (below the last row or on column headers).
/// </summary>
public static class DataGridExt
{
    public static readonly AttachedProperty<bool> DeselectOnEmptyClickProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>(
            "DeselectOnEmptyClick",
            typeof(DataGridExt));

    static DataGridExt()
    {
        DeselectOnEmptyClickProperty.Changed.AddClassHandler<DataGrid>(OnDeselectOnEmptyClickChanged);
    }

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
}
