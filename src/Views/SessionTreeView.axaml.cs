using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TermThing.Sessions;

namespace TermThing.Views;

public partial class SessionTreeView : UserControl
{
    // Raised when the user double-clicks a session to launch it
    public event EventHandler<SessionDefinition>? SessionLaunchRequested;

    // Raised when the user requests editing a session's settings
    public event EventHandler<SessionDefinition>? SessionEditRequested;

    // Raised when the tree data changes (add/rename/delete/move/duplicate)
    public event EventHandler? TreeChanged;

    // Clipboard for cut/copy/paste within the tree
    private SessionDefinition? _clipboard;
    private bool _isCut;

    private TreeView _tree = null!;

    // Context menu items whose enabled state depends on what is selected
    private readonly MenuItem _mnuRename;
    private readonly MenuItem _mnuDuplicate;
    private readonly MenuItem _mnuCut;
    private readonly MenuItem _mnuCopy;
    private readonly MenuItem _mnuPaste;
    private readonly MenuItem _mnuDelete;

    // The root group to display (set by MainWindow after loading config)
    public SessionGroup? RootGroup { get; private set; }

    public SessionTreeView()
    {
        InitializeComponent();

        _tree = this.FindControl<TreeView>("SessionTree")!;

        // Build the context menu in code so we hold direct references to
        // selection-sensitive items and can enable/disable them reliably.
        var mnuNewLocal   = new MenuItem { Header = "🖥 Local…"  }; mnuNewLocal.Click   += (_, _) => NewSession(SessionKind.Local);
        var mnuNewSsh     = new MenuItem { Header = "🌐 SSH…"    }; mnuNewSsh.Click     += (_, _) => NewSession(SessionKind.Ssh);
        var mnuNewSerial  = new MenuItem { Header = "🔌 Serial…" }; mnuNewSerial.Click  += (_, _) => NewSession(SessionKind.Serial);

        var mnuNewSession = new MenuItem { Header = "New Session" };
        mnuNewSession.Items.Add(mnuNewLocal);
        mnuNewSession.Items.Add(mnuNewSsh);
        mnuNewSession.Items.Add(mnuNewSerial);

        var mnuNewSubgroup = new MenuItem { Header = "New Subgroup" };
        mnuNewSubgroup.Click += OnNewSubgroupClicked;

        _mnuRename    = new MenuItem { Header = "Rename"    }; _mnuRename.Click    += OnRenameClicked;
        _mnuDuplicate = new MenuItem { Header = "Duplicate" }; _mnuDuplicate.Click += OnDuplicateClicked;
        _mnuCut       = new MenuItem { Header = "Cut"       }; _mnuCut.Click       += OnCutClicked;
        _mnuCopy      = new MenuItem { Header = "Copy"      }; _mnuCopy.Click      += OnCopyClicked;
        _mnuPaste     = new MenuItem { Header = "Paste"     }; _mnuPaste.Click     += OnPasteClicked;
        _mnuDelete    = new MenuItem { Header = "Delete"    }; _mnuDelete.Click    += OnDeleteClicked;

        var mnuEdit = new MenuItem { Header = "Edit…" };
        mnuEdit.Click += OnEditClicked;

        var menu = new ContextMenu();
        menu.Opening += OnContextMenuOpening;
        menu.Items.Add(mnuNewSession);
        menu.Items.Add(mnuNewSubgroup);
        menu.Items.Add(new Separator());
        menu.Items.Add(mnuEdit);
        menu.Items.Add(_mnuRename);
        menu.Items.Add(_mnuDuplicate);
        menu.Items.Add(new Separator());
        menu.Items.Add(_mnuCut);
        menu.Items.Add(_mnuCopy);
        menu.Items.Add(_mnuPaste);
        menu.Items.Add(new Separator());
        menu.Items.Add(_mnuDelete);

        _tree.ContextMenu = menu;

        // Right-click should select the item under the cursor before the
        // context menu opens (Avalonia only selects on left-click by default).
        _tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
    }

    public void SetRoot(SessionGroup root)
    {
        RootGroup = root;
        RebuildTree();
    }

    // -----------------------------------------------------------------------
    // Pointer / selection
    // -----------------------------------------------------------------------

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsRightButtonPressed)
        {
            // Walk up from the event source to find the nearest TreeViewItem so
            // that SelectedItem is correct when OnContextMenuOpening fires.
            var tvi = (e.Source as Control)?.FindAncestorOfType<TreeViewItem>();
            _tree.SelectedItem = tvi?.DataContext;
        }
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var node     = _tree.SelectedItem as SessionTreeNode;
        bool hasAny  = node is not null;
        bool isSess  = node?.IsSession == true;
        bool canDel  = hasAny && (isSess || (node!.Tag is SessionGroup g && g != RootGroup));

        _mnuRename.IsEnabled    = hasAny;
        _mnuDuplicate.IsEnabled = isSess;
        _mnuCut.IsEnabled       = isSess;
        _mnuCopy.IsEnabled      = isSess;
        _mnuPaste.IsEnabled     = _clipboard is not null;
        _mnuDelete.IsEnabled    = canDel;
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
            SessionLaunchRequested?.Invoke(this, def);
    }

    // -----------------------------------------------------------------------
    // Context menu handlers
    // -----------------------------------------------------------------------

    private void NewSession(SessionKind kind)
    {
        var group = SelectedGroup() ?? RootGroup!;
        var def = new SessionDefinition
        {
            Kind     = kind,
            Name     = $"New {kind} Session",
            GroupId  = group.Id,
            Settings = kind switch
            {
                SessionKind.Local  => new LocalSettings(),
                SessionKind.Ssh    => new SshSettings(),
                SessionKind.Serial => new SerialSettings(),
                _                  => null,
            },
        };
        group.Sessions.Add(def);
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnNewSubgroupClicked(object? sender, RoutedEventArgs e)
    {
        var parent = SelectedGroup() ?? RootGroup!;
        parent.Subgroups.Add(new SessionGroup { Name = "New Group" });
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnRenameClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode node) return;

        var currentName = node.Tag switch
        {
            SessionDefinition d => d.Name,
            SessionGroup g      => g.Name,
            _                   => string.Empty,
        };

        var dialog = new RenameDialog(currentName);
        var result = await dialog.ShowDialog<string?>(TopLevel.GetTopLevel(this) as Window);
        if (result is null) return;

        switch (node.Tag)
        {
            case SessionDefinition d: d.Name = result; break;
            case SessionGroup      g: g.Name = result; break;
        }

        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDuplicateClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode { Tag: SessionDefinition def }) return;
        (FindGroupContaining(def, RootGroup!) ?? RootGroup!).Sessions.Add(def.Duplicate());
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is not SessionTreeNode node) return;

        if (!await ConfirmDeleteAsync(node.Header)) return;

        switch (node.Tag)
        {
            case SessionDefinition def:
                RemoveSession(def, RootGroup!);
                break;
            case SessionGroup grp when grp != RootGroup:
                RemoveGroup(grp, RootGroup!);
                break;
        }

        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCutClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
        {
            _clipboard = def;
            _isCut = true;
        }
    }

    private void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
        {
            _clipboard = def.Duplicate();
            _isCut = false;
        }
    }

    private void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        if (_clipboard is null) return;
        var target = SelectedGroup() ?? RootGroup!;

        if (_isCut)
        {
            RemoveSession(_clipboard, RootGroup!);
            _isCut = false;
        }
        else
        {
            _clipboard = _clipboard.Duplicate();
        }

        _clipboard.GroupId = target.Id;
        target.Sessions.Add(_clipboard);
        _clipboard = null;
        RebuildTree();
        TreeChanged?.Invoke(this, EventArgs.Empty);
    }

    // -----------------------------------------------------------------------
    // Tree building
    // -----------------------------------------------------------------------

    private void OnEditClicked(object? sender, RoutedEventArgs e)
    {
        if (_tree.SelectedItem is SessionTreeNode { Tag: SessionDefinition def })
            SessionEditRequested?.Invoke(this, def);
    }

    private void RebuildTree()
    {
        if (RootGroup is null) return;
        var root = WrapGroup(RootGroup);
        _tree.ItemsSource = new[] { root };
    }

    private static SessionTreeNode WrapGroup(SessionGroup group)
    {
        // Read persisted expansion state; default to true (expanded) when not yet recorded.
        bool expanded = Configuration.SettingsService.Temp.SessionTreeExpansion
            .GetValueOrDefault(group.Id, defaultValue: true);

        var node = new SessionTreeNode
        {
            Header     = group.Name,
            Tag        = group,
            IsExpanded = expanded,
        };
        foreach (var sub in group.Subgroups)
            node.Children.Add(WrapGroup(sub));
        foreach (var s in group.Sessions)
            node.Children.Add(new SessionTreeNode { Header = $"{KindIcon(s.Kind)} {s.Name}", Tag = s });
        return node;
    }

    private static string KindIcon(SessionKind k) => k switch
    {
        SessionKind.Local  => "🖥",
        SessionKind.Ssh    => "🌐",
        SessionKind.Serial => "🔌",
        _                  => "•",
    };

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private SessionGroup? SelectedGroup()
    {
        if (_tree?.SelectedItem is SessionTreeNode { Tag: SessionGroup g }) return g;
        return null;
    }

    private static SessionGroup? FindGroupContaining(SessionDefinition def, SessionGroup root)
    {
        if (root.Sessions.Contains(def)) return root;
        foreach (var sub in root.Subgroups)
        {
            var found = FindGroupContaining(def, sub);
            if (found is not null) return found;
        }
        return null;
    }

    private static bool RemoveSession(SessionDefinition def, SessionGroup root)
    {
        if (root.Sessions.Remove(def)) return true;
        foreach (var sub in root.Subgroups)
            if (RemoveSession(def, sub)) return true;
        return false;
    }

    private static bool RemoveGroup(SessionGroup grp, SessionGroup root)
    {
        if (root.Subgroups.Remove(grp)) return true;
        foreach (var sub in root.Subgroups)
            if (RemoveGroup(grp, sub)) return true;
        return false;
    }

    private async Task<bool> ConfirmDeleteAsync(string name)
    {
        var win = new Window
        {
            Title = "Confirm Delete",
            Width = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SizeToContent = SizeToContent.Height,
            Content = new StackPanel
            {
                Margin  = new Avalonia.Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"Delete '{name}'?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation         = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button { Content = "Delete" },
                            new Button { Content = "Cancel" },
                        }
                    }
                }
            }
        };
        var buttons   = (StackPanel)((StackPanel)win.Content!).Children[1];
        var deleteBtn = (Button)buttons.Children[0];
        var cancelBtn = (Button)buttons.Children[1];
        var tcs = new TaskCompletionSource<bool>();
        deleteBtn.Click += (_, _) => { tcs.TrySetResult(true);  win.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        await win.ShowDialog(TopLevel.GetTopLevel(this) as Window ?? throw new InvalidOperationException());
        return await tcs.Task;
    }
}
