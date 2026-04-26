using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TermThing.Sessions;

namespace TermThing.Views;

/// <summary>Data item for the sessions tree. Groups and sessions are both represented.</summary>
public class SessionTreeNode : INotifyPropertyChanged
{
    private bool _isExpanded;

    public required string Header { get; init; }
    public required object Tag { get; init; }   // SessionDefinition or SessionGroup
    public ObservableCollection<SessionTreeNode> Children { get; } = [];

    public bool IsGroup   => Tag is SessionGroup;
    public bool IsSession => Tag is SessionDefinition;

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
