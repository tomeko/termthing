using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;
using System.Linq;
using TermThing.Sessions;

namespace TermThing.Views;

/// <summary>
/// Simple dialog that presents a list of saved SSH sessions and lets the user
/// pick one to use as a jump-host reference.
/// </summary>
public sealed class SessionPickerDialog : Window
{
    private readonly ListBox _list;

    public SessionPickerDialog(IReadOnlyList<SessionDefinition> sessions)
    {
        Title  = "Select session as jump host";
        Width  = 360;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list = new ListBox
        {
            ItemsSource   = sessions,
            MaxHeight     = 300,
            Margin        = new Avalonia.Thickness(0, 0, 0, 8),
        };
        _list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SessionDefinition>(
            (def, _) => new TextBlock { Text = def.Name, Margin = new Avalonia.Thickness(2) }, true);

        var ok     = new Button { Content = "Select", IsDefault = true, Margin = new Avalonia.Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel  = true };
        ok.Click     += (_, _) => { if (_list.SelectedItem is SessionDefinition) Close(_list.SelectedItem); };
        cancel.Click += (_, _) => Close(null);
        _list.DoubleTapped += (_, _) => { if (_list.SelectedItem is SessionDefinition) Close(_list.SelectedItem); };

        Content = new StackPanel
        {
            Margin  = new Avalonia.Thickness(16),
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Choose an SSH session to use as a jump hop:", Margin = new Avalonia.Thickness(0,0,0,6) },
                _list,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel },
                },
            },
        };
    }
}
