using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using FontStyle = Avalonia.Media.FontStyle;
using Material.Icons;
using TermThing.Sessions;

namespace TermThing.Views;

/// <summary>Data item for the sessions tree. Groups and sessions are both represented.</summary>
public class SessionTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public required string Header { get; init; }
    public required object Tag { get; init; }   // SessionDefinition or SessionGroup
    public ObservableCollection<SessionTreeNode> Children { get; } = [];

    /// <summary>Resolved icon kind for this node (group = Folder, session = custom or kind-default).</summary>
    public MaterialIconKind IconKindEnum { get; init; } = MaterialIconKind.Folder;

    /// <summary>Brush for the icon. Never null — returns a default gray when no custom color is set.</summary>
    public IBrush IconBrush { get; init; } = new SolidColorBrush(Color.Parse("#BDBDBD"));

    public bool IsGroup   => Tag is SessionGroup;
    public bool IsSession => Tag is SessionDefinition;

    /// <summary>
    /// True when this node is part of a read-only group (the group itself or any
    /// ancestor was marked <see cref="SessionGroup.IsReadOnly"/>). Set by
    /// <see cref="SessionTreeView"/> when it rebuilds the tree. The XAML template
    /// uses this to render the row in italics.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>Italic when read-only, normal otherwise — bound from the item template.</summary>
    public FontStyle FontStyle => IsReadOnly ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>Tooltip shown for read-only nodes — typically the source file path.</summary>
    public string? ReadOnlyTooltip { get; init; }

    /// <summary>Total recursive session count (set when wrapping a group node).</summary>
    public int ChildCount { get; init; }

    public string ChildCountLabel => ChildCount == 1 ? "(1 session)" : $"({ChildCount} sessions)";

    /// <summary>True when this is a collapsed group with more than 1 descendant session.</summary>
    public bool ShowChildCount => IsGroup && !IsExpanded && ChildCount > 1;

    /// <summary>
    /// Raised when <see cref="IsExpanded"/> changes so the TwoWay binding in the
    /// <see cref="TreeView"/> can reflect the state back into the model.
    /// The setter also persists the change to <see cref="Configuration.SettingsService.Temp"/>
    /// so expansion survives restarts.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowChildCount));

            // Persist group expansion state — session nodes are leaves and don't need this.
            if (Tag is SessionGroup group)
            {
                Configuration.SettingsService.Temp.SessionTreeExpansion[group.Id] = value;
                Configuration.SettingsService.SaveTemp();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
